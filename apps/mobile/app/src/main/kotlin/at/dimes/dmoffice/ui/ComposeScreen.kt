package at.dimes.dmoffice.ui

import android.content.Context
import android.text.format.Formatter
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.draw.clip
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.automirrored.outlined.StickyNote2
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.outlined.Cloud
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Image
import androidx.compose.material.icons.outlined.PhoneAndroid
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.InputChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.Draft
import at.dimes.dmoffice.api.Message
import at.dimes.dmoffice.files.FileItem
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** Vorbelegung des Editors (neu, Antwort, Weiterleitung); wird als JSON in der Route uebergeben. */
@Serializable
data class ComposeInit(
    val account: String, val to: String = "", val cc: String = "", val subject: String = "", val body: String = "",
    val inReplyTo: String? = null, val references: List<String> = emptyList(),
    val replyFolder: String? = null, val replyUid: Long? = null,
    val forwardFolder: String? = null, val forwardUid: Long? = null, val forwardAttachments: List<Int> = emptyList(),
) {
    fun encode(): String = java.net.URLEncoder.encode(Json.encodeToString(this), "UTF-8")

    companion object {
        fun decode(s: String): ComposeInit = Json.decodeFromString(java.net.URLDecoder.decode(s, "UTF-8"))

        private fun quote(ctx: Context, m: Message): String =
            "\n\n" + ctx.getString(R.string.wrote_on, formatDate(ctx, m.date), m.from?.full ?: "") + "\n" + m.text.lines().joinToString("\n") { "> $it" }

        fun reply(ctx: Context, account: String, m: Message, all: Boolean): ComposeInit {
            val me = App.get(ctx).prefs.account.value?.login ?: ""
            val replyTo = (m.replyTo.ifEmpty { listOfNotNull(m.from) }).map { it.full }
            val others = if (all) (m.to + m.cc).filter { !it.address.equals(me, true) && replyTo.none { r -> r.contains(it.address, true) } }.map { it.full } else emptyList()
            val refs = (m.references + listOfNotNull(m.messageId)).distinct()
            return ComposeInit(account, to = replyTo.joinToString(", "), cc = others.joinToString(", "),
                subject = if (m.subject.startsWith("Re:", true)) m.subject else "Re: " + m.subject,
                body = quote(ctx, m), inReplyTo = m.messageId, references = refs, replyFolder = m.folder, replyUid = m.uid)
        }

        fun forward(ctx: Context, account: String, m: Message): ComposeInit = ComposeInit(
            account, subject = if (m.subject.startsWith("Fwd:", true) || m.subject.startsWith("WG:", true)) m.subject else "Fwd: " + m.subject,
            body = "\n\n---------- " + ctx.getString(R.string.forwarded_message) + " ----------\n" +
                ctx.getString(R.string.from_label) + ": " + (m.from?.full ?: "") + "\n" + ctx.getString(R.string.date_label) + ": " + formatDate(ctx, m.date) + "\n" +
                ctx.getString(R.string.subject) + ": " + m.subject + "\n" + ctx.getString(R.string.to) + ": " + m.to.joinToString(", ") { it.full } + "\n\n" + m.text,
            forwardFolder = m.folder, forwardUid = m.uid, forwardAttachments = m.attachments.filter { !it.inline }.map { it.index },
        )
    }
}

/** Ab dieser Groesse geht ein Anhang von selbst als Freigabelink (je Anhang umstellbar) - wie im Web. */
private const val LINK_FROM = 10L * 1024 * 1024

/** Ein Anhang: vom Geraet (Bytes sind schon da) oder aus der Dateiablage (der Server haengt ihn an). */
/** asLink: als Freigabelink statt in der Mail (Ablauf und Abrufe nach den Vorgaben unter Freigabelinks). */
private sealed class Attachment {
    abstract val name: String
    abstract val size: Long
    abstract val asLink: Boolean
    abstract fun linked(on: Boolean): Attachment
    data class Local(override val name: String, val mime: String, val bytes: ByteArray, override val asLink: Boolean = false) : Attachment() {
        override val size get() = bytes.size.toLong()
        override fun linked(on: Boolean) = copy(asLink = on)
    }
    data class Stored(val path: String, override val name: String, override val size: Long, override val asLink: Boolean = false) : Attachment() {
        override fun linked(on: Boolean) = copy(asLink = on)
    }
}

/** Neue Mail schreiben; gesendet wird ueber den Server (SMTP des Kontos, Kopie in Gesendet). */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun ComposeScreen(init: ComposeInit, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var to by remember { mutableStateOf(init.to) }
    var cc by remember { mutableStateOf(init.cc) }
    var bcc by remember { mutableStateOf("") }
    var showCc by remember { mutableStateOf(init.cc.isNotBlank()) }
    var subject by remember { mutableStateOf(init.subject) }
    var body by remember { mutableStateOf(init.body) }
    var attachments by remember { mutableStateOf<List<Attachment>>(emptyList()) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var from by remember { mutableStateOf<String?>(null) }
    var attachMenu by remember { mutableStateOf(false) }
    var pickFiles by remember { mutableStateOf(false) }
    var pickNote by remember { mutableStateOf(false) }

    LaunchedEffect(init.account) {
        from = try { app.mail.accounts().firstOrNull { it.id == init.account }?.let { it.email.ifBlank { it.name } } } catch (_: Exception) { null }
    }

    fun addUris(uris: List<android.net.Uri>) {
        for (u in uris) try {
            val name = ctx.contentResolver.query(u, null, null, null, null)?.use { c ->
                val i = c.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME); if (c.moveToFirst() && i >= 0) c.getString(i) else null
            } ?: u.lastPathSegment ?: "Anhang"
            val bytes = ctx.contentResolver.openInputStream(u)?.use { it.readBytes() } ?: continue
            attachments = attachments + Attachment.Local(name, ctx.contentResolver.getType(u) ?: "application/octet-stream", bytes, asLink = bytes.size >= LINK_FROM)
        } catch (e: Exception) { error = e.message }
    }
    val documents = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { addUris(it) }
    val media = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia()) { addUris(it) }

    fun send() {
        if (to.isBlank() && cc.isBlank() && bcc.isBlank()) { error = ctx.getString(R.string.no_recipient); return }
        busy = true; error = null
        scope.launch {
            try {
                val html = "<div style=\"font-family:sans-serif;font-size:14px\">" + android.text.Html.escapeHtml(body).replace("\n", "<br>") + "</div>"
                // Namen im Formular eindeutig machen - der Server erkennt Links an ihnen
                val used = mutableSetOf<String>()
                val local = attachments.filterIsInstance<Attachment.Local>().map { a ->
                    val base = a.name.substringBeforeLast('.')
                    val ext = if ('.' in a.name) "." + a.name.substringAfterLast('.') else ""
                    var n = a.name; var k = 2
                    while (!used.add(n)) { n = "$base ($k)$ext"; k++ }
                    a to n
                }
                val stored = attachments.filterIsInstance<Attachment.Stored>()
                val warning = app.mail.send(init.account, Draft(to, cc, bcc, subject, html, init.inReplyTo, init.references, init.replyFolder, init.replyUid,
                    init.forwardFolder, init.forwardUid, init.forwardAttachments,
                    filePaths = stored.filter { !it.asLink }.map { it.path }, linkPaths = stored.filter { it.asLink }.map { it.path },
                    linkUploads = local.filter { it.first.asLink }.map { it.second }),
                    local.map { (a, n) -> Triple(n, a.mime, a.bytes) })
                android.widget.Toast.makeText(ctx, if (warning == null) ctx.getString(R.string.sent_ok) else ctx.getString(R.string.sent_warning, warning), android.widget.Toast.LENGTH_LONG).show()
                onClose()
            } catch (e: Exception) { error = e.message } finally { busy = false }
        }
    }

    Scaffold(topBar = {
        TopAppBar(
            title = { Text(subject.ifBlank { stringResource(R.string.compose) }, maxLines = 1, overflow = TextOverflow.Ellipsis) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                Box {
                    IconButton(onClick = { attachMenu = true }) { Icon(Icons.Default.AttachFile, contentDescription = stringResource(R.string.attach_file)) }
                    DropdownMenu(expanded = attachMenu, onDismissRequest = { attachMenu = false }) {
                        DropdownMenuItem(text = { Text(stringResource(R.string.attach_photo)) }, leadingIcon = { Icon(Icons.Outlined.Image, null) },
                            onClick = { attachMenu = false; media.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo)) })
                        DropdownMenuItem(text = { Text(stringResource(R.string.attach_from_device)) }, leadingIcon = { Icon(Icons.Outlined.PhoneAndroid, null) },
                            onClick = { attachMenu = false; documents.launch(arrayOf("*/*")) })
                        DropdownMenuItem(text = { Text(stringResource(R.string.attach_from_files)) }, leadingIcon = { Icon(Icons.Outlined.Cloud, null) },
                            onClick = { attachMenu = false; pickFiles = true })
                        DropdownMenuItem(text = { Text(stringResource(R.string.attach_note)) }, leadingIcon = { Icon(Icons.AutoMirrored.Outlined.StickyNote2, null) },
                            onClick = { attachMenu = false; pickNote = true })
                    }
                }
                IconButton(onClick = { send() }, enabled = !busy) {
                    if (busy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    else Icon(Icons.AutoMirrored.Filled.Send, contentDescription = stringResource(R.string.send), tint = MaterialTheme.colorScheme.primary)
                }
            }
        )
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState())) {
            from?.let { HeaderRow(stringResource(R.string.from_label)) { Text(it, style = MaterialTheme.typography.bodyLarge, modifier = Modifier.padding(vertical = 14.dp)) } }
            HeaderRow(stringResource(R.string.to)) {
                PlainField(to, { to = it }, Modifier.weight(1f), KeyboardType.Email)
                IconButton(onClick = { showCc = !showCc }) {
                    Icon(if (showCc) Icons.Default.KeyboardArrowUp else Icons.Default.KeyboardArrowDown, contentDescription = stringResource(R.string.cc))
                }
            }
            if (showCc) {
                HeaderRow(stringResource(R.string.cc)) { PlainField(cc, { cc = it }, Modifier.weight(1f), KeyboardType.Email) }
                HeaderRow(stringResource(R.string.bcc)) { PlainField(bcc, { bcc = it }, Modifier.weight(1f), KeyboardType.Email) }
            }
            HeaderRow(stringResource(R.string.subject)) { PlainField(subject, { subject = it }, Modifier.weight(1f)) }
            if (attachments.isNotEmpty() || init.forwardAttachments.isNotEmpty()) {
                FlowRow(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    if (init.forwardAttachments.isNotEmpty())
                        AssistChip(onClick = {}, label = { Text(stringResource(R.string.attachments) + ": " + init.forwardAttachments.size) }, leadingIcon = { Icon(Icons.Default.AttachFile, null, Modifier.size(18.dp)) })
                    for ((i, a) in attachments.withIndex()) Box {
                        var menu by remember { mutableStateOf(false) }
                        InputChip(selected = a.asLink, onClick = { menu = true },
                            label = { Text(a.name + " · " + Formatter.formatShortFileSize(ctx, a.size), maxLines = 1, overflow = TextOverflow.Ellipsis) },
                            leadingIcon = { Icon(if (a.asLink) Icons.Default.Link else if (a is Attachment.Stored) Icons.Outlined.Cloud else Icons.Default.AttachFile, null, Modifier.size(18.dp)) })
                        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                            DropdownMenuItem(text = { Text(stringResource(R.string.attach_as_file)) }, leadingIcon = { Icon(Icons.Default.AttachFile, null) },
                                trailingIcon = { if (!a.asLink) Icon(Icons.Default.Check, null) },
                                onClick = { menu = false; attachments = attachments.mapIndexed { j, x -> if (j == i) x.linked(false) else x } })
                            DropdownMenuItem(text = { Text(stringResource(R.string.attach_as_link)) }, leadingIcon = { Icon(Icons.Default.Link, null) },
                                trailingIcon = { if (a.asLink) Icon(Icons.Default.Check, null) },
                                onClick = { menu = false; attachments = attachments.mapIndexed { j, x -> if (j == i) x.linked(true) else x } })
                            DropdownMenuItem(text = { Text(stringResource(R.string.remove)) }, leadingIcon = { Icon(Icons.Default.Close, null) },
                                onClick = { menu = false; attachments = attachments.filterIndexed { j, _ -> j != i } })
                        }
                    }
                }
                HorizontalDivider()
            }
            PlainField(body, { body = it }, Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp).heightIn(min = 280.dp), singleLine = false,
                placeholder = stringResource(R.string.message_body))
            error?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
        }
    }

    if (pickFiles) StoredFilePicker(onDismiss = { pickFiles = false }, onPick = { picked ->
        pickFiles = false
        attachments = attachments + picked.filter { p -> attachments.none { it is Attachment.Stored && it.path == p.path } }.map { Attachment.Stored(it.path, it.name, it.size, asLink = it.size >= LINK_FROM) }
    })
    if (pickNote) NotePicker(onDismiss = { pickNote = false }, onPick = { id ->
        pickNote = false
        scope.launch {
            try {
                val note = app.notes.load(id)
                val text = android.text.Html.fromHtml(note.blocks.joinToString(""), android.text.Html.FROM_HTML_MODE_COMPACT).toString().trim()
                if (text.isNotEmpty()) body = if (body.isBlank()) text else body.trimEnd() + "\n\n" + text
                // Bilder und Dateien der Notiz kommen als Anhang mit
                val extra = note.attachments.mapNotNull { a -> try { Attachment.Local(a.name, a.mime.ifBlank { "application/octet-stream" }, app.notes.blobAsync(a.blob)) } catch (_: Exception) { null } }
                attachments = attachments + extra
            } catch (e: Exception) { error = e.message }
        }
    })
}

/** Kopfzeile wie in Mail-Apps: Beschriftung links, Eingabe ohne Rahmen, Trennlinie darunter. */
@Composable
private fun HeaderRow(label: String, content: @Composable RowScope.() -> Unit) {
    Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyLarge, modifier = Modifier.width(64.dp))
        content()
    }
    HorizontalDivider(Modifier.padding(start = 16.dp))
}

/** Eingabefeld ohne Rahmen und Hintergrund. */
@Composable
private fun PlainField(value: String, onChange: (String) -> Unit, modifier: Modifier, keyboard: KeyboardType = KeyboardType.Text, singleLine: Boolean = true, placeholder: String? = null) {
    TextField(value = value, onValueChange = onChange, modifier = modifier, singleLine = singleLine,
        placeholder = placeholder?.let { { Text(it) } },
        keyboardOptions = KeyboardOptions(keyboardType = keyboard, capitalization = if (keyboard == KeyboardType.Email) KeyboardCapitalization.None else KeyboardCapitalization.Sentences),
        colors = TextFieldDefaults.colors(
            focusedContainerColor = Color.Transparent, unfocusedContainerColor = Color.Transparent, disabledContainerColor = Color.Transparent,
            focusedIndicatorColor = Color.Transparent, unfocusedIndicatorColor = Color.Transparent, disabledIndicatorColor = Color.Transparent,
        ))
}

/** Dateien aus der eigenen Dateiablage waehlen - ganzer Bildschirm, mit Vorschaubildern; Ordner oeffnen, Dateien ankreuzen. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun StoredFilePicker(onDismiss: () -> Unit, onPick: (List<FileItem>) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    var path by remember { mutableStateOf("") }
    var items by remember { mutableStateOf<List<FileItem>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    val picked = remember { mutableStateListOf<FileItem>() }
    LaunchedEffect(path) {
        items = null; error = null
        try { items = withContext(Dispatchers.IO) { app.files.list(path).items } } catch (e: Exception) { error = e.message; items = emptyList() }
    }
    val up = { if (path.isEmpty()) onDismiss() else path = path.substringBeforeLast('/', "") }
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        BackHandler { up() }
        Scaffold(topBar = {
            TopAppBar(
                title = { Text(if (path.isEmpty()) stringResource(R.string.files) else path.substringAfterLast('/'), maxLines = 1, overflow = TextOverflow.Ellipsis) },
                navigationIcon = { IconButton(onClick = up) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
                actions = { TextButton(enabled = picked.isNotEmpty(), onClick = { onPick(picked.toList()) }) { Text(stringResource(R.string.attach_count, picked.size)) } })
        }) { pad ->
            Column(Modifier.padding(pad).fillMaxSize()) {
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
                val list = items
                if (list == null) Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
                else LazyColumn(Modifier.fillMaxSize()) {
                    items(list, key = { it.path }) { f ->
                        val checked = picked.any { it.path == f.path }
                        Row(Modifier.fillMaxWidth().clickable {
                            if (f.isDir) path = f.path else if (checked) picked.removeAll { it.path == f.path } else picked.add(f)
                        }.padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                            ThumbOrIcon(f, Modifier.size(48.dp).clip(RoundedCornerShape(6.dp)), 28.dp)
                            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                                Text(f.name, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                val sub = listOfNotNull(if (f.isDir) null else Formatter.formatShortFileSize(ctx, f.size), f.modifiedMs.takeIf { it > 0 }?.let { Fmt.date(it) })
                                if (sub.isNotEmpty()) Text(sub.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                            if (!f.isDir) Checkbox(checked = checked, onCheckedChange = { if (it) picked.add(f) else picked.removeAll { p -> p.path == f.path } })
                        }
                    }
                }
            }
        }
    }
}

/** Notiz waehlen - ganzer Bildschirm: ihr Text kommt in die Nachricht, ihre Bilder und Dateien als Anhang. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun NotePicker(onDismiss: () -> Unit, onPick: (Long) -> Unit) {
    val app = App.get(LocalContext.current)
    var q by remember { mutableStateOf("") }
    val notes by remember(q) { app.notes.search(q) }.collectAsState(initial = emptyList())
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Scaffold(topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.attach_note)) },
                navigationIcon = { IconButton(onClick = onDismiss) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } })
        }) { pad ->
            Column(Modifier.padding(pad).fillMaxSize()) {
                OutlinedTextField(q, { q = it }, Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), singleLine = true,
                    placeholder = { Text(stringResource(R.string.search)) }, leadingIcon = { Icon(Icons.Default.Search, null) })
                LazyColumn(Modifier.fillMaxSize()) {
                    items(notes.filter { it.archived == null && it.deleted == null }, key = { it.id }) { n ->
                        Column(Modifier.fillMaxWidth().clickable { onPick(n.id) }.padding(horizontal = 16.dp, vertical = 10.dp)) {
                            Text(n.title.ifBlank { stringResource(R.string.note_untitled) }, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            if (n.snippet.isNotBlank()) Text(n.snippet, maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        HorizontalDivider(Modifier.padding(start = 16.dp))
                    }
                }
            }
        }
    }
}
