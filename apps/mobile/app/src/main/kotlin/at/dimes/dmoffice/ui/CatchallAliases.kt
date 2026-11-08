package at.dimes.dmoffice.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.AlternateEmail
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Language
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.LockOpen
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.AliasConfig
import at.dimes.dmoffice.api.AliasItem
import at.dimes.dmoffice.api.AliasSender
import at.dimes.dmoffice.api.AliasUpdate
import kotlinx.coroutines.launch

/** Catchall-Alias in die Zwischenablage, mit kurzer Meldung. */
private fun copyAlias(ctx: Context, address: String) {
    ctx.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("dmOffice", address))
    android.widget.Toast.makeText(ctx, ctx.getString(R.string.alias_copied, address), android.widget.Toast.LENGTH_SHORT).show()
}

/** Nach jeder Änderung an Catchall-Aliasen (Sperren, Anlegen, Übernehmen): Ordnerleiste und Liste laden neu. */
object CatchallSignal {
    val version = kotlinx.coroutines.flow.MutableStateFlow(0)
    fun bump() { version.value++ }
}

/** Klein unter Absender oder Betreff: „über alias@example.com“, dazu Sperren, Entsperren oder (unbekannte Adresse) Übernehmen. */
@Composable
fun AliasLine(alias: String?, state: String? = null, sender: String? = null) {
    if (alias.isNullOrBlank()) return
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var now by remember(alias, state) { mutableStateOf(state ?: "open") }
    var busy by remember { mutableStateOf(false) }
    var adopt by remember { mutableStateOf(false) }
    if (adopt) AdoptAliasDialog(alias, sender, onDismiss = { ok -> adopt = false; if (ok) now = "open" })
    Row(verticalAlignment = Alignment.CenterVertically) {
        if (now == "blocked") Icon(Icons.Default.Lock, null, Modifier.size(12.dp), tint = MaterialTheme.colorScheme.error)
        Text(stringResource(if (now == "unknown") R.string.alias_unknown_to else R.string.alias_via, alias), Modifier.weight(1f, fill = false), style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Spacer(Modifier.width(8.dp))
        Text(stringResource(when (now) { "unknown" -> R.string.alias_adopt; "blocked" -> R.string.alias_unblock; else -> R.string.alias_block }),
            style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.SemiBold,
            modifier = Modifier.clickable(enabled = !busy) {
                if (now == "unknown") { adopt = true; return@clickable }
                busy = true
                scope.launch {
                    try {
                        app.mail.aliasUpdate(AliasUpdate(alias, block = now != "blocked"))
                        now = if (now == "blocked") "open" else "blocked"
                        CatchallSignal.bump()
                    } catch (e: Exception) {
                        android.widget.Toast.makeText(ctx, e.message ?: "", android.widget.Toast.LENGTH_LONG).show()
                    } finally { busy = false }
                }
            }.padding(vertical = 2.dp))
    }
}

/** Unbekannte Adresse als Alias anlegen: Notiz abfragen (vorbelegt mit dem Absendernamen), dann Ordner anlegen und alle Mails an
 *  diese Adresse aus dem Sammelordner der Domain dorthin verschieben (der Server erledigt beides). */
@Composable
fun AdoptAliasDialog(address: String, sender: String?, onDismiss: (Boolean) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var note by remember { mutableStateOf(sender ?: "") }
    var busy by remember { mutableStateOf(false) }
    AlertDialog(onDismissRequest = { onDismiss(false) }, title = { Text(stringResource(R.string.alias_adopt)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(address, fontWeight = FontWeight.SemiBold)
                OutlinedTextField(note, { note = it }, Modifier.fillMaxWidth(), singleLine = true, label = { Text(stringResource(R.string.alias_note)) },
                    supportingText = { Text(stringResource(R.string.alias_adopt_hint)) })
            }
        },
        confirmButton = { TextButton(enabled = !busy, onClick = {
            busy = true
            scope.launch {
                try {
                    val at = address.lastIndexOf('@')
                    val a = app.mail.aliasCreate(address.substring(0, at), address.substring(at + 1), note.trim())
                    android.widget.Toast.makeText(ctx, ctx.getString(R.string.alias_adopted, address) +
                        (if ((a.moved ?: 0) > 0) " · " + ctx.getString(R.string.alias_moved, a.moved) else ""), android.widget.Toast.LENGTH_SHORT).show()
                    CatchallSignal.bump()
                    onDismiss(true)
                } catch (e: Exception) {
                    android.widget.Toast.makeText(ctx, e.message ?: "", android.widget.Toast.LENGTH_LONG).show()
                    busy = false
                }
            }
        }) { Text(stringResource(R.string.alias_create_short)) } },
        dismissButton = { TextButton(onClick = { onDismiss(false) }) { Text(stringResource(R.string.cancel)) } })
}

/** Entfernen nach Zeit für neue Mails an einen Alias: aus, in den Papierkorb oder vernichten nach n Tagen (MailExpiry auf dem Server). */
@Composable
fun AliasExpiryDialog(a: AliasItem, onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var kind by remember { mutableStateOf(a.expireKind ?: "trash") }
    var days by remember { mutableStateOf((a.expireDays ?: 30).toString()) }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.alias_expire_menu)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(a.address, fontWeight = FontWeight.SemiBold)
                for ((k, label) in listOf("" to R.string.alias_expire_off, "trash" to R.string.alias_expire_trash_label, "destroy" to R.string.alias_expire_destroy_label))
                    Row(Modifier.fillMaxWidth().clickable { kind = k }, verticalAlignment = Alignment.CenterVertically) {
                        androidx.compose.material3.RadioButton(selected = kind == k, onClick = { kind = k })
                        Text(stringResource(label))
                    }
                if (kind.isNotEmpty()) OutlinedTextField(days, { days = it.filter(Char::isDigit).take(4) }, Modifier.fillMaxWidth(), singleLine = true,
                    label = { Text(stringResource(R.string.alias_expire_days)) }, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    supportingText = { Text(stringResource(if (kind == "destroy") R.string.alias_expire_hint_destroy else R.string.alias_expire_hint)) })
            }
        },
        confirmButton = { TextButton(onClick = {
            scope.launch {
                try { app.mail.aliasUpdate(AliasUpdate(a.address, expireKind = kind, expireDays = if (kind.isEmpty()) null else days.toIntOrNull())); CatchallSignal.bump(); onDismiss() }
                catch (e: Exception) { android.widget.Toast.makeText(ctx, e.message ?: "", android.widget.Toast.LENGTH_LONG).show() }
            }
        }) { Text(stringResource(R.string.save)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

/** Notiz eines Alias ändern. */
@Composable
fun AliasNoteDialog(a: AliasItem, onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var note by remember { mutableStateOf(a.note) }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(a.address) },
        text = { OutlinedTextField(note, { note = it }, Modifier.fillMaxWidth(), singleLine = true, label = { Text(stringResource(R.string.alias_note)) }) },
        confirmButton = { TextButton(onClick = {
            scope.launch {
                try { app.mail.aliasUpdate(AliasUpdate(a.address, note = note)); CatchallSignal.bump() }
                catch (e: Exception) { android.widget.Toast.makeText(ctx, e.message ?: "", android.widget.Toast.LENGTH_LONG).show() }
                onDismiss()
            }
        }) { Text(stringResource(R.string.save)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

/** Abschnitt „Catchall-Aliase“ in der Ordnerleiste (wie die Kategorien): je Domain ihr Ordner, darunter je Alias seiner mit Zähler,
 *  gesperrte mit Schloss. Langes Drücken auf einen Alias: Sperren/Entsperren, Notiz ändern, Adresse kopieren. */
/** Name mit Notiz in Klammern: „shop (Notiz)“, ohne Notiz nur der Name. */
fun withNote(name: String, note: String?) = note?.trim()?.takeIf { it.isNotEmpty() }?.let { "$name ($it)" } ?: name

/** Titel für einen Catchall-Ordner: beim Alias die Adresse mit Notiz, beim Domain-Ordner die Domain; sonst null. */
fun catchallTitle(st: at.dimes.dmoffice.api.AliasState?, account: String, path: String): String? {
    if (st == null || st.config.account != account) return null
    st.items.firstOrNull { it.folder == path }?.let { return withNote(it.address, it.note) }
    return st.domains.firstOrNull { it.folder == path }?.domain
}

@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
@Composable
fun CatchallDrawerSection(st: at.dimes.dmoffice.api.AliasState, folders: List<at.dimes.dmoffice.api.Folder>, current: String, onFolder: (String) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var open by remember { mutableStateOf(true) }
    var noteFor by remember { mutableStateOf<AliasItem?>(null) }
    var deleteFor by remember { mutableStateOf<AliasItem?>(null) }
    Row(Modifier.fillMaxWidth().clickable { open = !open }.padding(horizontal = 24.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(stringResource(R.string.alias_title), style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
        Icon(if (open) Icons.Default.KeyboardArrowUp else Icons.Default.KeyboardArrowDown, contentDescription = null)
    }
    if (!open) return
    fun folderOf(path: String?) = path?.let { p -> folders.firstOrNull { it.path == p } }
    @Composable
    fun entry(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, path: String?, depth: Int, tint: androidx.compose.ui.graphics.Color,
              alias: AliasItem? = null, total: Boolean = false) {
        val f = folderOf(path)
        var menu by remember { mutableStateOf(false) }
        Box {
            Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp).clip(androidx.compose.foundation.shape.RoundedCornerShape(12.dp))
                .background(if (f != null && f.path == current) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surface.copy(alpha = 0f))
                .combinedClickable(onClick = { if (f != null) onFolder(f.path) }, onLongClick = if (alias != null) ({ menu = true }) else null)
                .padding(start = (14 + depth * 16).dp, end = 14.dp, top = 12.dp, bottom = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(icon, contentDescription = null, tint = tint)
                Spacer(Modifier.width(18.dp))
                Text(label, style = MaterialTheme.typography.bodyLarge, fontWeight = if ((f?.unread ?: 0) > 0) FontWeight.SemiBold else FontWeight.Normal,
                    color = if (f == null) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                    maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                val n = if (total) f?.total ?: 0 else f?.unread ?: 0
                if (n > 0) Text(n.toString(), style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (alias != null) DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                DropdownMenuItem(text = { Text(stringResource(if (alias.blocked != null) R.string.alias_unblock else R.string.alias_block)) },
                    leadingIcon = { Icon(if (alias.blocked != null) Icons.Default.LockOpen else Icons.Default.Lock, null) },
                    onClick = { menu = false; scope.launch {
                        try { app.mail.aliasUpdate(AliasUpdate(alias.address, block = alias.blocked == null)); CatchallSignal.bump() }
                        catch (e: Exception) { android.widget.Toast.makeText(ctx, e.message ?: "", android.widget.Toast.LENGTH_LONG).show() }
                    } })
                DropdownMenuItem(text = { Text(stringResource(R.string.alias_edit_note)) }, leadingIcon = { Icon(Icons.Default.Edit, null) },
                    onClick = { menu = false; noteFor = alias })
                DropdownMenuItem(text = { Text(stringResource(R.string.alias_copy)) }, leadingIcon = { Icon(Icons.Default.ContentCopy, null) },
                    onClick = { menu = false; copyAlias(ctx, alias.address) })
                DropdownMenuItem(text = { Text(stringResource(R.string.alias_delete_menu)) }, leadingIcon = { Icon(Icons.Default.Delete, null) },
                    onClick = { menu = false; deleteFor = alias })
            }
        }
    }
    for (d in st.domains) {
        entry(Icons.Default.Language, d.domain, d.folder, 0, MaterialTheme.colorScheme.primary, total = true)
        for (a in st.items.filter { it.address.endsWith("@" + d.domain) })
            entry(if (a.blocked != null) Icons.Default.Lock else Icons.Default.AlternateEmail, withNote(a.address.substringBeforeLast('@'), a.note), a.folder, 1,
                if (a.blocked != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant, a)
    }
    noteFor?.let { AliasNoteDialog(it, onDismiss = { noteFor = null }) }
    deleteFor?.let { a -> AliasDeleteDialog(a.address, onDismiss = { deleteFor = null }, onConfirm = {
        deleteFor = null
        scope.launch {
            try { app.mail.aliasDelete(a.address); CatchallSignal.bump() }
            catch (e: Exception) { android.widget.Toast.makeText(ctx, e.message ?: "", android.widget.Toast.LENGTH_LONG).show() }
        }
    }) }
}

/** Rückfrage vor dem Löschen eines Alias: Mails aus seinem Ordner in den Ordner der Domain, sein Ordner wird gelöscht (Server). */
@Composable
fun AliasDeleteDialog(address: String, onDismiss: () -> Unit, onConfirm: () -> Unit) {
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.alias_delete_title, address)) },
        text = { Text(stringResource(R.string.alias_delete_text, address)) },
        confirmButton = { TextButton(onClick = onConfirm) { Text(stringResource(R.string.delete)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

/** Neuer Catchall-Alias (wie web/src/mail/AliasDialog.tsx): Namen selbst wählen oder zufällig, Notiz „wofür“, danach kopieren. */
@Composable
fun NewAliasDialog(config: AliasConfig, onDismiss: () -> Unit, onCreated: (AliasItem) -> Unit = {}) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var name by remember { mutableStateOf("") }
    var note by remember { mutableStateOf("") }
    var domain by remember { mutableStateOf(config.domains.firstOrNull() ?: "") }
    var domainMenu by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    fun shuffle() = scope.launch { try { name = app.mail.aliasSuggest() } catch (e: Exception) { error = e.message } }
    LaunchedEffect(Unit) { shuffle() }
    AlertDialog(onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.alias_new)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(name, { name = it; error = null }, Modifier.weight(1f), singleLine = true, label = { Text(stringResource(R.string.alias_name)) },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email, capitalization = KeyboardCapitalization.None))
                    IconButton(onClick = { shuffle() }) { Icon(Icons.Default.Shuffle, contentDescription = stringResource(R.string.alias_random)) }
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    TextButton(onClick = { if (config.domains.size > 1) domainMenu = true }) { Text("@$domain") }
                    DropdownMenu(expanded = domainMenu, onDismissRequest = { domainMenu = false }) {
                        for (d in config.domains) DropdownMenuItem(text = { Text("@$d") }, onClick = { domain = d; domainMenu = false })
                    }
                }
                OutlinedTextField(note, { note = it }, Modifier.fillMaxWidth(), singleLine = true, label = { Text(stringResource(R.string.alias_note)) },
                    supportingText = { Text(stringResource(R.string.alias_note_hint)) })
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = {
            TextButton(enabled = !busy, onClick = {
                busy = true
                scope.launch {
                    try {
                        val a = app.mail.aliasCreate(name.trim(), domain, note)
                        copyAlias(ctx, a.address)
                        onCreated(a); onDismiss()
                    } catch (e: Exception) { error = e.message } finally { busy = false }
                }
            }) { Text(stringResource(R.string.alias_create)) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

/** Catchall-Aliase aus der Mail-Ansicht: Liste mit Übersicht, Sperren mit einem Tipp, neue Adresse. Domain und Postfach stellt man im Web ein. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AliasesDialog(onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var config by remember { mutableStateOf<AliasConfig?>(null) }
    var items by remember { mutableStateOf<List<AliasItem>>(emptyList()) }
    var error by remember { mutableStateOf<String?>(null) }
    var adding by remember { mutableStateOf(false) }
    var open by remember { mutableStateOf<String?>(null) }
    var senders by remember { mutableStateOf<List<AliasSender>?>(null) }
    suspend fun load() { try { val st = app.mail.aliases(); config = st.config; items = st.items } catch (e: Exception) { error = e.message } }
    LaunchedEffect(Unit) { load() }
    fun update(body: AliasUpdate) = scope.launch {
        try { val a = app.mail.aliasUpdate(body); items = items.map { if (it.address == a.address) a else it }; CatchallSignal.bump() } catch (e: Exception) { error = e.message }
    }
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        Scaffold(topBar = {
            TopAppBar(title = { Text(stringResource(R.string.alias_title)) },
                navigationIcon = { IconButton(onClick = onDismiss) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
                actions = {
                    val c = config
                    if (c != null && c.domains.isNotEmpty() && c.account != null)
                        IconButton(onClick = { adding = true }) { Icon(Icons.Default.Add, contentDescription = stringResource(R.string.alias_new)) }
                })
        }) { pad ->
            Column(Modifier.padding(pad).fillMaxSize()) {
                error?.let { Text(stringResource(R.string.error_generic, it), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
                val c = config
                when {
                    c == null -> CircularProgressIndicator(Modifier.padding(24.dp))
                    c.domains.isEmpty() || c.account == null -> Text(stringResource(R.string.alias_setup), Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
                    items.isEmpty() -> Text(stringResource(R.string.alias_none), Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
                    else -> LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                        items(items, key = { it.address }) { a ->
                            AliasRow(a, open == a.address, senders, onClick = {
                                if (open == a.address) open = null
                                else { open = a.address; senders = null; scope.launch { senders = try { app.mail.aliasDetails(a.address).senders } catch (e: Exception) { error = e.message; emptyList() } } }
                            }, onCopy = { copyAlias(ctx, a.address) },
                                onBlock = { blockAs -> update(AliasUpdate(a.address, block = blockAs != null, blockAs = blockAs)) },
                                onRemove = { scope.launch { try { app.mail.aliasDelete(a.address); items = items.filter { it.address != a.address }; CatchallSignal.bump() } catch (e: Exception) { error = e.message } } })
                            HorizontalDivider()
                        }
                    }
                }
            }
        }
    }
    config?.let { c -> if (adding) NewAliasDialog(c, onDismiss = { adding = false }, onCreated = { CatchallSignal.bump(); scope.launch { load() } }) }
}

@Composable
private fun AliasRow(a: AliasItem, expanded: Boolean, senders: List<AliasSender>?, onClick: () -> Unit, onCopy: () -> Unit,
                     onBlock: (String?) -> Unit, onRemove: () -> Unit) {
    val ctx = LocalContext.current
    var menu by remember { mutableStateOf(false) }
    var timing by remember { mutableStateOf(false) }
    var removing by remember { mutableStateOf(false) }
    if (timing) AliasExpiryDialog(a, onDismiss = { timing = false })
    if (removing) AliasDeleteDialog(a.address, onDismiss = { removing = false }, onConfirm = { removing = false; onRemove() })
    Column(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(start = 16.dp, end = 4.dp, top = 8.dp, bottom = 8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(a.address, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (a.blocked != null) Text(stringResource(if (a.blocked == "delete") R.string.alias_blocked_delete else R.string.alias_blocked_junk),
                    style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.error)
                else if (a.auto) Text(stringResource(R.string.alias_auto), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (a.note.isNotBlank()) Text(a.note, style = MaterialTheme.typography.bodyMedium)
                Text(listOfNotNull(stringResource(R.string.alias_created, formatDate(ctx, a.created)), stringResource(R.string.alias_count, a.count),
                    a.last?.let { stringResource(R.string.alias_last, formatDate(ctx, it)) },
                    a.expireDays?.let { d -> stringResource(if (a.expireKind == "destroy") R.string.alias_expire_destroy else R.string.alias_expire_trash, d) }
                        ?.takeIf { a.expireKind != null }).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            IconButton(onClick = onCopy) { Icon(Icons.Default.ContentCopy, contentDescription = stringResource(R.string.alias_copy)) }
            IconButton(onClick = { onBlock(if (a.blocked != null) null else "") }) {
                Icon(if (a.blocked != null) Icons.Default.LockOpen else Icons.Default.Lock,
                    contentDescription = stringResource(if (a.blocked != null) R.string.alias_unblock else R.string.alias_block),
                    tint = if (a.blocked != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
            }
            IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                DropdownMenuItem(text = { Text(stringResource(R.string.alias_block_junk)) }, onClick = { menu = false; onBlock("junk") })
                DropdownMenuItem(text = { Text(stringResource(R.string.alias_block_delete)) }, onClick = { menu = false; onBlock("delete") })
                DropdownMenuItem(text = { Text(stringResource(R.string.alias_expire_menu)) }, onClick = { menu = false; timing = true })
                DropdownMenuItem(text = { Text(stringResource(R.string.alias_delete_menu)) }, onClick = { menu = false; removing = true })
            }
        }
        if (expanded) {
            Spacer(Modifier.height(4.dp))
            Text(stringResource(R.string.alias_senders), style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.SemiBold)
            when {
                senders == null -> CircularProgressIndicator(Modifier.size(18.dp).padding(top = 4.dp), strokeWidth = 2.dp)
                senders.isEmpty() -> Text(stringResource(R.string.alias_no_mails), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                else -> for (s in senders) Row {
                    Text((if (s.name.isNullOrBlank()) s.address else "${s.name} <${s.address}>"), Modifier.weight(1f), style = MaterialTheme.typography.bodySmall,
                        maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Spacer(Modifier.width(8.dp))
                    Text(s.count.toString(), style = MaterialTheme.typography.bodySmall)
                }
            }
        }
    }
}
