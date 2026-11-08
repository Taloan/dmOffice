package at.dimes.dmoffice.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.StickyNote2
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Save
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Close
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.chat.ChatFileItem
import at.dimes.dmoffice.chat.ChatMessage
import at.dimes.dmoffice.chat.ChatNoteFolder
import at.dimes.dmoffice.chat.ChatNoteItem
import at.dimes.dmoffice.chat.ChatRoom
import at.dimes.dmoffice.chat.ChatSettings
import kotlinx.coroutines.launch

/** Palette fuer Unterhaltungen - dieselbe wie im Web (Kalender, Listen). */
internal val chatColors = listOf("#0F6CBD", "#13A10E", "#CA5010", "#C239B3", "#E3008C", "#986F0B", "#038387", "#8764B8", "#D13438", "#69797E")

internal fun chatColorOf(hex: String?): Color? = try { hex?.let { Color(android.graphics.Color.parseColor(it)) } } catch (_: Exception) { null }

/** Zeigen (nur ansehen) oder Senden (darf speichern). */
@Composable
internal fun ModeChoice(mode: String, onChange: (String) -> Unit) {
    Column {
        for ((value, label) in listOf("show" to R.string.chat_mode_show_long, "send" to R.string.chat_mode_send_long))
            Row(Modifier.fillMaxWidth().clickable { onChange(value) }, verticalAlignment = Alignment.CenterVertically) {
                RadioButton(selected = mode == value, onClick = { onChange(value) })
                Text(stringResource(label))
            }
    }
}

/** Auswahl im Vollbild: oben Titel und Schliessen, die Liste nutzt die ganze Hoehe, unten Art und Knopf. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun FullScreenPicker(title: String, onDismiss: () -> Unit, bottom: @Composable () -> Unit, content: @Composable androidx.compose.foundation.layout.ColumnScope.() -> Unit) {
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss, properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false)) {
        androidx.compose.material3.Surface(Modifier.fillMaxSize()) {
            Column(Modifier.fillMaxSize()) {
                TopAppBar(title = { Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    navigationIcon = { IconButton(onClick = onDismiss) { Icon(Icons.Default.Close, stringResource(android.R.string.cancel)) } })
                Column(Modifier.weight(1f).fillMaxWidth(), content = content)
                androidx.compose.material3.HorizontalDivider()
                Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) { bottom() }
            }
        }
    }
}

/** Ordner-Zeile mit Knopf nach oben fuer die Ablage. */
@Composable
private fun PathBar(path: String, onUp: () -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        IconButton(enabled = path.isNotEmpty(), onClick = onUp) { Icon(Icons.Default.ArrowUpward, stringResource(R.string.chat_up)) }
        Icon(Icons.Default.Folder, null, Modifier.size(18.dp))
        Text(path.ifEmpty { stringResource(R.string.files) }, Modifier.padding(start = 6.dp), maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

/** Mini-Vorschau fuer Bilder (auch PDF, Video), sonst das Symbol. */
@Composable
private fun FileThumb(f: ChatFileItem) {
    val app = App.get(LocalContext.current)
    val ext = f.name.substringAfterLast('.', "").lowercase()
    val mime = f.mime ?: ""
    val thumbable = mime.startsWith("image/") || mime.startsWith("video/") || mime == "application/pdf" ||
        ext in setOf("jpg", "jpeg", "png", "gif", "webp", "heic", "pdf", "mp4", "mov")
    var bmp by remember(f.path) { mutableStateOf<android.graphics.Bitmap?>(null) }
    if (thumbable) LaunchedEffect(f.path) { bmp = app.chat.fileThumb(f) }
    Box(Modifier.size(44.dp).clip(androidx.compose.foundation.shape.RoundedCornerShape(6.dp)), contentAlignment = Alignment.Center) {
        val b = bmp
        if (b != null) androidx.compose.foundation.Image(b.asImageBitmap(), null, Modifier.fillMaxSize(), contentScale = androidx.compose.ui.layout.ContentScale.Crop)
        else Icon(Icons.Default.Description, null)
    }
}

/** Bei mir speichern: Ziel waehlen - Ordner der Ablage (Datei, Vorgabe aus Einstellungen -> Chat) oder Notizordner (Notiz, Vorgabe „Chat“). Vollbild. */
@Composable
internal fun SaveTargetDialog(isNote: Boolean, name: String, onDismiss: () -> Unit, onSave: (String?) -> Unit) {
    val app = App.get(LocalContext.current)
    var path by remember { mutableStateOf<String?>(null) }
    var dirs by remember { mutableStateOf<List<ChatFileItem>?>(null) }
    var folders by remember { mutableStateOf<List<ChatNoteFolder>?>(null) }
    // Notizen: Vorgabe ist der Ordner „Chat“ (#chat - der Server legt ihn bei Bedarf an), "" = ohne Ordner
    var noteFolder by remember { mutableStateOf("#chat") }
    LaunchedEffect(Unit) {
        val s = try { app.chat.settings().settings } catch (_: Exception) { ChatSettings() }
        if (isNote) folders = try { app.chat.noteFolders().filter { !(it.parent == null && it.name == "Chat") } } catch (_: Exception) { emptyList() }
        else path = s.saveFolder
    }
    LaunchedEffect(path) {
        val p = path ?: return@LaunchedEffect
        dirs = null
        dirs = try { app.chat.listFiles(p).filter { it.isDir } } catch (_: Exception) { emptyList() }
    }
    val folderLabel = (path ?: "").ifEmpty { stringResource(R.string.files) }
    FullScreenPicker(stringResource(R.string.chat_save_title, name), onDismiss, bottom = {
        androidx.compose.material3.Button(onClick = { onSave(if (isNote) noteFolder.takeIf { it != "#chat" } else path ?: "") }, modifier = Modifier.fillMaxWidth()) {
            Text(if (isNote) stringResource(R.string.chat_save_here) else stringResource(R.string.chat_save_in, folderLabel))
        }
    }) {
        if (isNote) {
            val list = folders
            if (list == null) CircularProgressIndicator(Modifier.padding(16.dp))
            else LazyColumn(Modifier.fillMaxSize()) {
                item {
                    ListItem(headlineContent = { Text(stringResource(R.string.chat_note_folder_chat)) },
                        leadingContent = { RadioButton(selected = noteFolder == "#chat", onClick = { noteFolder = "#chat" }) },
                        modifier = Modifier.clickable { noteFolder = "#chat" })
                }
                item {
                    ListItem(headlineContent = { Text(stringResource(R.string.chat_no_folder)) },
                        leadingContent = { RadioButton(selected = noteFolder == "", onClick = { noteFolder = "" }) },
                        modifier = Modifier.clickable { noteFolder = "" })
                }
                items(list, key = { it.syncId }) { f ->
                    ListItem(headlineContent = { Text(f.name) },
                        leadingContent = { RadioButton(selected = noteFolder == f.syncId, onClick = { noteFolder = f.syncId }) },
                        modifier = Modifier.clickable { noteFolder = f.syncId })
                }
            }
        } else {
            PathBar(path ?: "") { path = path?.substringBeforeLast('/', "") }
            val list = dirs
            if (list == null) CircularProgressIndicator(Modifier.padding(16.dp))
            else if (list.isEmpty()) Text(stringResource(R.string.chat_no_subfolders), color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(16.dp))
            else LazyColumn(Modifier.fillMaxSize()) {
                items(list, key = { it.path }) { d ->
                    ListItem(headlineContent = { Text(d.name) }, leadingContent = { Icon(Icons.Default.Folder, null) }, modifier = Modifier.clickable { path = d.path })
                }
            }
        }
    }
}

/** Weiterleiten in andere Unterhaltungen; Gezeigtes nur als Zeigen (und nur, wenn der Absender es erlaubt - prueft der Server). */
@Composable
internal fun ForwardDialog(message: ChatMessage, rooms: List<ChatRoom>, onDismiss: () -> Unit, onDone: (List<ChatMessage>) -> Unit, onError: (String?) -> Unit) {
    val app = App.get(LocalContext.current)
    val scope = rememberCoroutineScope()
    val onlyShow = message.refs.any { it.shown }
    var chosen by remember { mutableStateOf(setOf<Long>()) }
    // Personen ohne Unterhaltung zu zweit: beim Weiterleiten eine neue anfangen
    val people = rememberPeopleWithoutChat()
    var chosenPeople by remember { mutableStateOf(setOf<String>()) }
    var mode by remember { mutableStateOf(if (onlyShow) "show" else "send") }
    AlertDialog(onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.chat_forward)) },
        text = {
            Column {
                if (rooms.isEmpty() && people.isEmpty()) Text(stringResource(R.string.chat_no_other_rooms))
                LazyColumn(Modifier.heightIn(max = 300.dp)) {
                    items(rooms, key = { it.id }) { r ->
                        Row(Modifier.fillMaxWidth().clickable { chosen = if (r.id in chosen) chosen - r.id else chosen + r.id }, verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(checked = r.id in chosen, onCheckedChange = { chosen = if (it) chosen + r.id else chosen - r.id })
                            Text(r.title)
                        }
                    }
                    items(people, key = { "p" + it.id }) { p -> PersonRow(p, p.id in chosenPeople) { on -> chosenPeople = if (on) chosenPeople + p.id else chosenPeople - p.id } }
                }
                if (message.refs.isNotEmpty()) {
                    if (onlyShow) Text(stringResource(R.string.chat_forward_only_show), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    else ModeChoice(mode) { mode = it }
                }
            }
        },
        confirmButton = {
            TextButton(enabled = chosen.isNotEmpty() || chosenPeople.isNotEmpty(), onClick = {
                scope.launch {
                    try {
                        val fresh = chosenPeople.map { app.chat.open(listOf(it), null).id }
                        onDone(app.chat.forward(message.id, chosen.toList() + fresh, if (message.refs.isEmpty()) null else mode))
                    }
                    catch (e: Exception) { onError(e.message) }
                }
            }) { Text(stringResource(R.string.chat_forward)) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(android.R.string.cancel)) } })
}

/** Personen auf dem Server, mit denen es noch keine Unterhaltung zu zweit gibt (zum Weiterleiten und Verschicken). */
@Composable
internal fun rememberPeopleWithoutChat(): List<at.dimes.dmoffice.chat.ChatPerson> {
    val app = App.get(LocalContext.current)
    var people by remember { mutableStateOf<List<at.dimes.dmoffice.chat.ChatPerson>>(emptyList()) }
    LaunchedEffect(Unit) {
        people = try {
            val withDirect = app.chat.rooms().filter { it.kind == "direct" }.flatMap { r -> r.members.map { it.id } }.toSet()
            app.chat.people().filter { it.id !in withDirect }
        } catch (_: Exception) { emptyList() }
    }
    return people
}

/** Zeile „Person - neue Unterhaltung“ mit Haken. */
@Composable
internal fun PersonRow(p: at.dimes.dmoffice.chat.ChatPerson, on: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().clickable { onChange(!on) }, verticalAlignment = Alignment.CenterVertically) {
        Checkbox(checked = on, onCheckedChange = { onChange(it) })
        Text(p.displayName.ifBlank { p.login }, Modifier.weight(1f))
        Text(stringResource(R.string.chat_new_conversation), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** Datei aus der eigenen Ablage waehlen (mehrere moeglich), Art vorbelegt aus den Einstellungen. Vollbild. */
@Composable
internal fun FilePickDialog(onDismiss: () -> Unit, onPick: (List<ChatFileItem>, String) -> Unit) {
    val app = App.get(LocalContext.current)
    var path by remember { mutableStateOf("") }
    var items by remember { mutableStateOf<List<ChatFileItem>?>(null) }
    // Gewählt (auch aus anderen Ordnern): Kennung → Eintrag, für den Namen in der Anhang-Leiste
    var picked by remember { mutableStateOf(mapOf<Long, ChatFileItem>()) }
    val chosen = picked.keys
    var mode by remember { mutableStateOf("show") }
    LaunchedEffect(Unit) { try { mode = app.chat.settings().settings.filesMode } catch (_: Exception) { } }
    LaunchedEffect(path) { items = null; items = try { app.chat.listFiles(path) } catch (_: Exception) { emptyList() } }
    FullScreenPicker(stringResource(R.string.chat_from_files), onDismiss, bottom = {
        ModeChoice(mode) { mode = it }
        androidx.compose.material3.Button(enabled = chosen.isNotEmpty(), onClick = { onPick(picked.values.toList(), mode) }, modifier = Modifier.fillMaxWidth()) {
            Text(if (chosen.size > 1) stringResource(R.string.chat_add_n, chosen.size) else stringResource(R.string.chat_add))
        }
    }) {
        PathBar(path) { path = path.substringBeforeLast('/', "") }
        val list = items
        if (list == null) CircularProgressIndicator(Modifier.padding(16.dp))
        else LazyColumn(Modifier.fillMaxSize()) {
            items(list, key = { it.path }) { f ->
                if (f.isDir) ListItem(headlineContent = { Text(f.name) }, leadingContent = { Icon(Icons.Default.Folder, null, Modifier.size(28.dp)) },
                    modifier = Modifier.clickable { path = f.path })
                else ListItem(
                    headlineContent = { Text(f.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    supportingContent = { Text(android.text.format.Formatter.formatShortFileSize(LocalContext.current, f.size)) },
                    leadingContent = { FileThumb(f) },
                    trailingContent = { Checkbox(checked = f.fileId in chosen, onCheckedChange = { picked = if (it) picked + (f.fileId to f) else picked - f.fileId }) },
                    modifier = Modifier.clickable { picked = if (f.fileId in chosen) picked - f.fileId else picked + (f.fileId to f) })
            }
        }
    }
}

/** Eigene Notiz waehlen. Vollbild. */
@Composable
internal fun NotePickDialog(onDismiss: () -> Unit, onPick: (ChatNoteItem, String) -> Unit) {
    val app = App.get(LocalContext.current)
    var query by remember { mutableStateOf("") }
    var notes by remember { mutableStateOf<List<ChatNoteItem>?>(null) }
    var mode by remember { mutableStateOf("show") }
    LaunchedEffect(Unit) { try { mode = app.chat.settings().settings.itemsMode } catch (_: Exception) { } }
    LaunchedEffect(query) { kotlinx.coroutines.delay(250); notes = try { app.chat.listNotes(query) } catch (_: Exception) { emptyList() } }
    FullScreenPicker(stringResource(R.string.chat_send_note), onDismiss, bottom = { ModeChoice(mode) { mode = it } }) {
        OutlinedTextField(query, { query = it }, Modifier.fillMaxWidth().padding(horizontal = 16.dp), singleLine = true,
            placeholder = { Text(stringResource(R.string.chat_search_notes)) })
        val list = notes
        if (list == null) CircularProgressIndicator(Modifier.padding(16.dp))
        else LazyColumn(Modifier.fillMaxSize()) {
            items(list, key = { it.id }) { n ->
                ListItem(headlineContent = { Text(n.title, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    supportingContent = { Text(n.snippet, maxLines = 2, overflow = TextOverflow.Ellipsis) },
                    leadingContent = { Icon(Icons.AutoMirrored.Filled.StickyNote2, null) },
                    modifier = Modifier.clickable { onPick(n, mode) })
            }
        }
    }
}

/** Fotos aus der Galerie von dmOffice (eigene und Galerie-Ordner, neueste zuerst) zum Anhängen. Vollbild. */
@Composable
internal fun GalleryPickDialog(onDismiss: () -> Unit, onPick: (List<at.dimes.dmoffice.files.FileItem>, String) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    var items by remember { mutableStateOf<List<at.dimes.dmoffice.files.FileItem>?>(null) }
    var picked by remember { mutableStateOf(listOf<String>()) }
    var mode by remember { mutableStateOf("show") }
    LaunchedEffect(Unit) { try { mode = app.chat.settings().settings.filesMode } catch (_: Exception) { } }
    LaunchedEffect(Unit) {
        items = try { kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { app.files.photos() }.sortedByDescending { app.files.photoMetaCache[it.path]?.taken ?: it.modifiedMs } }
        catch (_: Exception) { emptyList() }
    }
    FullScreenPicker(stringResource(R.string.chat_gallery_dm), onDismiss, bottom = {
        ModeChoice(mode) { mode = it }
        androidx.compose.material3.Button(enabled = picked.isNotEmpty(), onClick = { onPick(picked.mapNotNull { p -> items?.firstOrNull { it.path == p } }, mode) },
            modifier = Modifier.fillMaxWidth()) {
            Text(if (picked.size > 1) stringResource(R.string.chat_add_n, picked.size) else stringResource(R.string.chat_add))
        }
    }) {
        val list = items
        if (list == null) CircularProgressIndicator(Modifier.padding(16.dp))
        else if (list.isEmpty()) Text(stringResource(R.string.files_photos_empty), Modifier.padding(16.dp))
        else androidx.compose.foundation.lazy.grid.LazyVerticalGrid(androidx.compose.foundation.lazy.grid.GridCells.Adaptive(96.dp), Modifier.fillMaxSize(),
            horizontalArrangement = Arrangement.spacedBy(2.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            items(list.size, key = { list[it].path }) { i ->
                val f = list[i]
                val bmp by androidx.compose.runtime.produceState(at.dimes.dmoffice.files.Thumbs.cached(f, at.dimes.dmoffice.files.Thumbs.SMALL), f.path) {
                    if (value == null) value = try { at.dimes.dmoffice.files.Thumbs.load(ctx, f, at.dimes.dmoffice.files.Thumbs.SMALL) } catch (_: Exception) { null }
                }
                val n = picked.indexOf(f.path)
                Box(Modifier.aspectRatio(1f).background(MaterialTheme.colorScheme.surfaceVariant).clickable { picked = if (n >= 0) picked - f.path else picked + f.path }) {
                    bmp?.let { androidx.compose.foundation.Image(it.asImageBitmap(), f.name, Modifier.fillMaxSize(), contentScale = androidx.compose.ui.layout.ContentScale.Crop) }
                    if (n >= 0) Box(Modifier.align(Alignment.TopEnd).padding(4.dp).size(24.dp).clip(CircleShape).background(MaterialTheme.colorScheme.primary),
                        contentAlignment = Alignment.Center) { Text("${n + 1}", color = MaterialTheme.colorScheme.onPrimary, style = MaterialTheme.typography.labelSmall) }
                }
            }
        }
    }
}

/** Farbe der Unterhaltung (persoenlich); "Akzentfarbe" nimmt die eigene Farbe zurueck. */
@Composable
internal fun ColorDialog(current: String?, onDismiss: () -> Unit, onPick: (String?) -> Unit) {
    AlertDialog(onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.chat_color)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                for (row in chatColors.chunked(5)) Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    for (c in row) Box(Modifier.size(36.dp).clip(CircleShape).background(chatColorOf(c) ?: Color.Gray)
                        .border(if (current.equals(c, ignoreCase = true)) 3.dp else 0.dp, MaterialTheme.colorScheme.onSurface, CircleShape)
                        .clickable { onPick(c) })
                }
            }
        },
        confirmButton = { TextButton(onClick = { onPick(null) }) { Text(stringResource(R.string.chat_color_accent)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(android.R.string.cancel)) } })
}

/** Einstellungen -> Chat (wie im Web) als eigener Bildschirm, etwa aus dem Chat heraus. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChatSettingsScreen(onClose: () -> Unit) {
    Scaffold(topBar = {
        TopAppBar(title = { Text(stringResource(R.string.chat)) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.chat_back)) } })
    }) { pad -> ChatSettingsContent(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState())) }
}

/** Benachrichtigungen bei neuen Chat-Nachrichten: eigener Schalter, dazu die Erlaubnis von Android, falls sie fehlt. */
@Composable
private fun ChatNotifySection() {
    val ctx = LocalContext.current
    var on by remember { mutableStateOf(at.dimes.dmoffice.notify.ChatNotify.enabled(ctx)) }
    var system by remember { mutableStateOf(at.dimes.dmoffice.notify.ChatNotify.allowedBySystem(ctx)) }
    val ask = androidx.activity.compose.rememberLauncherForActivityResult(androidx.activity.result.contract.ActivityResultContracts.RequestPermission()) {
        system = at.dimes.dmoffice.notify.ChatNotify.allowedBySystem(ctx)
    }
    androidx.compose.material3.ListItem(
        headlineContent = { Text(stringResource(R.string.chat_notify_title)) },
        supportingContent = { Text(stringResource(if (on && !system) R.string.chat_notify_blocked else R.string.chat_notify_hint)) },
        trailingContent = { androidx.compose.material3.Switch(checked = on, onCheckedChange = { on = it; at.dimes.dmoffice.notify.ChatNotify.setEnabled(ctx, it) }) })
    if (on && !system) androidx.compose.material3.TextButton(onClick = {
        if (android.os.Build.VERSION.SDK_INT >= 33 && ctx.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED)
            ask.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        else ctx.startActivity(android.content.Intent(android.provider.Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS)
            .putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, ctx.packageName)
            .putExtra(android.provider.Settings.EXTRA_CHANNEL_ID, at.dimes.dmoffice.notify.ChatNotify.CHANNEL)
            .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK))
    }) { Text(stringResource(R.string.chat_notify_allow)) }
}

/** Inhalt der Chat-Einstellungen - auch als Unterseite der App-Einstellungen; rollt nicht selbst. */
@Composable
fun ChatSettingsContent(modifier: Modifier = Modifier) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var s by remember { mutableStateOf<ChatSettings?>(null) }
    var expiringAllowed by remember { mutableStateOf(true) }
    var folders by remember { mutableStateOf<List<ChatNoteFolder>>(emptyList()) }
    var durationMenu by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        try { val r = app.chat.settings(); s = r.settings; expiringAllowed = r.rights.expiring } catch (e: Exception) { android.widget.Toast.makeText(ctx, e.message, android.widget.Toast.LENGTH_SHORT).show() }
        folders = try { app.chat.noteFolders() } catch (_: Exception) { emptyList() }
    }
    fun save(next: ChatSettings) {
        s = next
        scope.launch { try { s = app.chat.saveSettings(next) } catch (e: Exception) { android.widget.Toast.makeText(ctx, e.message, android.widget.Toast.LENGTH_SHORT).show() } }
    }
    val durations = listOf("forever" to R.string.chat_dur_forever, "1y" to R.string.chat_dur_1y, "6m" to R.string.chat_dur_6m,
        "1m" to R.string.chat_dur_1m, "1w" to R.string.chat_dur_1w, "24h" to R.string.chat_dur_24h)
        val cur = s
        if (cur == null) { Box(modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }; return }
        Column(modifier.padding(horizontal = 16.dp)) {
            ChatNotifySection()
            ProfilePhotoSection()

            SectionHeader(stringResource(R.string.chat_set_default_mode))
            Text(stringResource(R.string.chat_set_default_mode_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(stringResource(R.string.chat_set_from_device), Modifier.padding(top = 8.dp))
            ModeChoice(cur.deviceMode) { save(cur.copy(deviceMode = it)) }
            Text(stringResource(R.string.chat_set_from_files), Modifier.padding(top = 8.dp))
            ModeChoice(cur.filesMode) { save(cur.copy(filesMode = it)) }
            Text(stringResource(R.string.chat_set_from_items), Modifier.padding(top = 8.dp))
            ModeChoice(cur.itemsMode) { save(cur.copy(itemsMode = it)) }

            SectionHeader(stringResource(R.string.chat_set_what_i_show))
            Box {
                ListItem(headlineContent = { Text(stringResource(R.string.chat_set_visible)) },
                    supportingContent = {
                        Column {
                            Text(stringResource(durations.firstOrNull { it.first == cur.showFor }?.second ?: R.string.chat_dur_forever), fontWeight = androidx.compose.ui.text.font.FontWeight.SemiBold)
                            Text(stringResource(if (expiringAllowed) R.string.chat_set_visible_hint else R.string.chat_set_expiring_off))
                        }
                    },
                    modifier = Modifier.clickable(enabled = expiringAllowed) { durationMenu = true })
                androidx.compose.material3.DropdownMenu(expanded = durationMenu, onDismissRequest = { durationMenu = false }) {
                    for ((key, label) in durations) androidx.compose.material3.DropdownMenuItem(text = { Text(stringResource(label)) },
                        onClick = { durationMenu = false; save(cur.copy(showFor = key)) })
                }
            }
            ListItem(headlineContent = { Text(stringResource(R.string.chat_set_reshow)) },
                supportingContent = { Text(stringResource(R.string.chat_set_reshow_hint)) },
                trailingContent = { Switch(checked = cur.reshow, onCheckedChange = { save(cur.copy(reshow = it)) }) })

            // Speichern: Dateien in den gewählten Ordner, Notizen immer in den Notizordner „Chat“ (Notizbücher wechseln in notizi)
            SectionHeader(stringResource(R.string.chat_set_save_section))
            Text(stringResource(R.string.chat_set_save_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(bottom = 8.dp))

            SectionHeader(stringResource(R.string.chat_set_privacy))
            ListItem(headlineContent = { Text(stringResource(R.string.chat_read_receipts)) },
                supportingContent = { Text(stringResource(R.string.chat_read_receipts_hint)) },
                trailingContent = { Switch(checked = cur.readReceipts, onCheckedChange = { save(cur.copy(readReceipts = it)) }) })

            SectionHeader(stringResource(R.string.chat_set_quiet))
            ListItem(headlineContent = { Text(stringResource(R.string.chat_quiet)) },
                supportingContent = { Text(stringResource(R.string.chat_quiet_hint)) },
                trailingContent = { Switch(checked = cur.quiet, onCheckedChange = { save(cur.copy(quiet = it)) }) })
            if (cur.quiet) {
                var fromText by remember(cur.quietFrom) { mutableStateOf(cur.quietFrom) }
                var toText by remember(cur.quietTo) { mutableStateOf(cur.quietTo) }
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(fromText, { fromText = it }, Modifier.weight(1f), singleLine = true, label = { Text(stringResource(R.string.chat_quiet_from)) },
                        trailingIcon = { if (fromText != cur.quietFrom) TextButton(onClick = { save(cur.copy(quietFrom = fromText.trim())) }) { Text(stringResource(R.string.chat_apply)) } })
                    OutlinedTextField(toText, { toText = it }, Modifier.weight(1f), singleLine = true, label = { Text(stringResource(R.string.chat_quiet_to)) },
                        trailingIcon = { if (toText != cur.quietTo) TextButton(onClick = { save(cur.copy(quietTo = toText.trim())) }) { Text(stringResource(R.string.chat_apply)) } })
                }
                ListItem(headlineContent = { Text(stringResource(R.string.chat_quiet_mentions)) },
                    trailingContent = { Switch(checked = cur.quietMentions, onCheckedChange = { save(cur.copy(quietMentions = it)) }) })
            }

            SectionHeader(stringResource(R.string.chat_set_pukki))
            ListItem(headlineContent = { Text(stringResource(R.string.pukki_daily)) },
                trailingContent = { Switch(checked = cur.pukkiDaily, onCheckedChange = { save(cur.copy(pukkiDaily = it)) }) })
            if (cur.pukkiDaily) {
                var timeText by remember(cur.pukkiTime) { mutableStateOf(cur.pukkiTime) }
                OutlinedTextField(timeText, { timeText = it }, Modifier.fillMaxWidth(), singleLine = true, label = { Text(stringResource(R.string.pukki_time)) },
                    trailingIcon = { if (timeText != cur.pukkiTime) TextButton(onClick = { save(cur.copy(pukkiTime = timeText.trim())) }) { Text(stringResource(R.string.chat_apply)) } })
            }
            ListItem(headlineContent = { Text(stringResource(R.string.pukki_reminders)) },
                supportingContent = { Text(stringResource(R.string.pukki_hint)) },
                trailingContent = { Switch(checked = cur.pukkiReminders, onCheckedChange = { save(cur.copy(pukkiReminders = it)) }) })
            PukkiMailSwitch()
            PukkiFigureSection()

            SectionHeader(stringResource(R.string.chat_set_location))
            ListItem(headlineContent = { Text(stringResource(R.string.chat_set_location_allow)) },
                supportingContent = { Text(stringResource(R.string.chat_set_location_hint)) },
                trailingContent = { Switch(checked = cur.locationRequests, onCheckedChange = { save(cur.copy(locationRequests = it)) }) })
        }
}

// ---------------- Stufe 3: Termin, Aufgabe, Kontakt, Ordner ----------------

private fun kotlinx.serialization.json.JsonObject.str(key: String): String? =
    (this[key] as? kotlinx.serialization.json.JsonPrimitive)?.takeIf { it.isString }?.content

private fun kotlinx.serialization.json.JsonObject.bool(key: String): Boolean =
    (this[key] as? kotlinx.serialization.json.JsonPrimitive)?.content == "true"

/** Zeit aus dem Server (ISO, UTC) lesbar; allDay: nur das Datum. */
private fun whenText(iso: String?, allDay: Boolean): String {
    if (iso.isNullOrBlank()) return ""
    return try {
        val t = java.time.Instant.parse(iso).atZone(java.time.ZoneId.systemDefault())
        if (allDay) t.toLocalDate().format(java.time.format.DateTimeFormatter.ofLocalizedDate(java.time.format.FormatStyle.FULL))
        else t.format(java.time.format.DateTimeFormatter.ofLocalizedDateTime(java.time.format.FormatStyle.FULL, java.time.format.FormatStyle.SHORT))
    } catch (_: Exception) { iso }
}

/** Termin, Aufgabe oder Kontakt aus einer Nachricht ansehen; bei Senden "Bei mir speichern". */
@Composable
internal fun ItemViewDialog(message: Long, index: Int, canSave: Boolean, onDismiss: () -> Unit, onSave: () -> Unit) {
    val app = App.get(LocalContext.current)
    var item by remember { mutableStateOf<kotlinx.serialization.json.JsonObject?>(null) }
    var gone by remember { mutableStateOf(false) }
    LaunchedEffect(message, index) { try { item = app.chat.item(message, index) } catch (_: Exception) { gone = true } }
    val it = item
    val contact = it?.get("contact") as? kotlinx.serialization.json.JsonObject
    AlertDialog(onDismissRequest = onDismiss,
        title = { Text(contact?.str("displayName") ?: it?.str("title") ?: stringResource(R.string.chat_view)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                when {
                    gone -> Text(stringResource(R.string.chat_gone))
                    it == null -> CircularProgressIndicator()
                    else -> {
                        it.str("ownerName")?.let { o -> Text(stringResource(R.string.chat_from_person, o), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                        when (it.str("type")) {
                            "event" -> {
                                Text(whenText(it.str("start"), it.bool("allDay")))
                                if (it.bool("recurring")) Text(stringResource(R.string.chat_repeats), color = MaterialTheme.colorScheme.onSurfaceVariant)
                                it.str("location")?.let { l -> Text(l) }
                                it.str("description")?.let { d -> Text(d) }
                            }
                            "task" -> {
                                it.str("due")?.let { d -> Text(stringResource(R.string.chat_due, whenText(d, it.bool("dueAllDay")))) }
                                Text(stringResource(if (it.bool("done")) R.string.chat_done else R.string.chat_open), color = MaterialTheme.colorScheme.onSurfaceVariant)
                                it.str("description")?.let { d -> Text(d) }
                            }
                            else -> if (contact != null) {
                                contact.str("organization")?.takeIf { o -> o.isNotBlank() }?.let { o -> Text(o) }
                                for (key in listOf("emails", "phones")) (contact[key] as? kotlinx.serialization.json.JsonArray)?.forEach { v ->
                                    (v as? kotlinx.serialization.json.JsonObject)?.str("value")?.let { s -> Text(s) }
                                }
                            }
                        }
                    }
                }
            }
        },
        confirmButton = { if (canSave && item != null) TextButton(onClick = onSave) { Text(stringResource(R.string.chat_save_mine)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.close)) } })
}

/** Bei mir speichern fuer Termin/Aufgabe/Kontakt: eigene Sammlung waehlen. */
@Composable
internal fun CollectionPickDialog(type: String, name: String, onDismiss: () -> Unit, onSave: (String) -> Unit) {
    val app = App.get(LocalContext.current)
    var list by remember { mutableStateOf<List<at.dimes.dmoffice.chat.ChatCollection>?>(null) }
    var chosen by remember { mutableStateOf<Long?>(null) }
    LaunchedEffect(type) {
        list = try { app.chat.ownCollections(contacts = type == "contact", component = when (type) { "event" -> "VEVENT"; "task" -> "VTODO"; else -> null }) } catch (_: Exception) { emptyList() }
        chosen = list?.firstOrNull()?.id
    }
    AlertDialog(onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.chat_save_title, name), maxLines = 2, overflow = TextOverflow.Ellipsis) },
        text = {
            val l = list
            if (l == null) CircularProgressIndicator()
            else if (l.isEmpty()) Text(stringResource(R.string.chat_no_collection))
            else LazyColumn(Modifier.heightIn(max = 360.dp)) {
                items(l, key = { it.id }) { c ->
                    Row(Modifier.fillMaxWidth().clickable { chosen = c.id }, verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(selected = chosen == c.id, onClick = { chosen = c.id })
                        Box(Modifier.size(12.dp).clip(CircleShape).background(chatColorOf(c.color) ?: MaterialTheme.colorScheme.primary))
                        Text(c.name, Modifier.padding(start = 8.dp))
                    }
                }
            }
        },
        confirmButton = { TextButton(enabled = chosen != null, onClick = { onSave(chosen.toString()) }) { Text(stringResource(R.string.chat_save_here)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(android.R.string.cancel)) } })
}

/** Ordner aus einer Nachricht: blaettern, ansehen; bei Senden einzelne Dateien bei mir speichern. Vollbild. */
@Composable
internal fun FolderViewDialog(message: Long, index: Int, name: String, canSave: Boolean, canOpen: Boolean, onDismiss: () -> Unit,
                              onOpenFile: (ChatFolderEntryView) -> Unit, onMessage: (String?) -> Unit) {
    val app = App.get(LocalContext.current)
    val scope = rememberCoroutineScope()
    var path by remember { mutableStateOf("") }
    var items by remember { mutableStateOf<List<at.dimes.dmoffice.chat.ChatFolderEntry>?>(null) }
    LaunchedEffect(path) { items = null; items = try { app.chat.folder(message, index, path) } catch (e: Exception) { onMessage(e.message); emptyList() } }
    val ctx = LocalContext.current
    FullScreenPicker(name, onDismiss, bottom = {
        Text(stringResource(if (canSave) R.string.chat_folder_sent_hint else R.string.chat_folder_shown_hint), style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant)
    }) {
        PathBar(path) { path = path.substringBeforeLast('/', "") }
        val list = items
        if (list == null) CircularProgressIndicator(Modifier.padding(16.dp))
        else if (list.isEmpty()) Text(stringResource(R.string.chat_folder_empty), Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
        else LazyColumn(Modifier.fillMaxSize()) {
            items(list, key = { it.path }) { f ->
                if (f.isDir) ListItem(headlineContent = { Text(f.name) }, leadingContent = { Icon(Icons.Default.Folder, null, Modifier.size(28.dp)) },
                    modifier = Modifier.clickable { path = f.path })
                else ListItem(
                    headlineContent = { Text(f.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    supportingContent = { Text(android.text.format.Formatter.formatShortFileSize(ctx, f.size)) },
                    leadingContent = {
                        var bmp by remember(f.path) { mutableStateOf<android.graphics.Bitmap?>(null) }
                        val thumbable = (f.mime ?: "").let { m -> m.startsWith("image/") || m.startsWith("video/") || m == "application/pdf" }
                        if (thumbable) LaunchedEffect(f.path) { bmp = app.chat.folderThumb(message, index, f) }
                        Box(Modifier.size(44.dp).clip(androidx.compose.foundation.shape.RoundedCornerShape(6.dp)), contentAlignment = Alignment.Center) {
                            val b = bmp
                            if (b != null) androidx.compose.foundation.Image(b.asImageBitmap(), null, Modifier.fillMaxSize(), contentScale = androidx.compose.ui.layout.ContentScale.Crop)
                            else Icon(Icons.Default.Description, null)
                        }
                    },
                    trailingContent = {
                        if (canSave) IconButton(onClick = {
                            scope.launch { try { val s = app.chat.folderSave(message, index, f.path); onMessage(ctx.getString(R.string.chat_saved_file) + " · " + (s.path ?: "")) } catch (e: Exception) { onMessage(e.message) } }
                        }) { Icon(Icons.Default.Save, stringResource(R.string.chat_save_mine)) }
                    },
                    modifier = Modifier.clickable { onOpenFile(ChatFolderEntryView(f, canOpen)) })
            }
        }
    }
}

/** Datei aus einem Ordner, die geoeffnet werden soll (canOpen: an andere Apps geben erlaubt). */
internal data class ChatFolderEntryView(val entry: at.dimes.dmoffice.chat.ChatFolderEntry, val canOpen: Boolean)

/** Pukki darf meine Mails lesen (Einstellungen → Chat, Abschnitt Pukki). */
@Composable
private fun PukkiMailSwitch() {
    val ctx = LocalContext.current
    val app = at.dimes.dmoffice.App.get(ctx)
    val scope = rememberCoroutineScope()
    var on by remember { mutableStateOf<Boolean?>(null) }
    LaunchedEffect(Unit) { on = try { app.chat.pukkiMail() } catch (_: Exception) { null } }
    val value = on ?: return
    ListItem(headlineContent = { Text(stringResource(R.string.pukki_mail)) },
        supportingContent = { Text(stringResource(R.string.pukki_mail_hint)) },
        trailingContent = {
            Switch(checked = value, onCheckedChange = { v ->
                on = v
                scope.launch { try { app.chat.setPukkiMail(v) } catch (e: Exception) { on = !v; android.widget.Toast.makeText(ctx, e.message ?: "?", android.widget.Toast.LENGTH_SHORT).show() } }
            })
        })
}
