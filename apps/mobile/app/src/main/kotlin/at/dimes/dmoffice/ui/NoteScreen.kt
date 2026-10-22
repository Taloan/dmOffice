package at.dimes.dmoffice.ui

import android.content.Intent
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.union
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.FormatListBulleted
import androidx.compose.material.icons.automirrored.filled.Redo
import androidx.compose.material.icons.automirrored.filled.Undo
import androidx.compose.material.icons.filled.Archive
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.CheckBox
import androidx.compose.material.icons.filled.Code
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.DeleteForever
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.FormatBold
import androidx.compose.material.icons.filled.FormatItalic
import androidx.compose.material.icons.filled.FormatStrikethrough
import androidx.compose.material.icons.filled.FormatUnderlined
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.Restore
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.FileProvider
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.webkit.WebViewAssetLoader
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.ApiException
import at.dimes.dmoffice.api.NoteDetail
import at.dimes.dmoffice.api.NoteMeta
import at.dimes.dmoffice.data.NoteMerge
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import java.io.ByteArrayInputStream
import java.io.File

/** Herkunft der Editorseite: Seite und Bilder kommen ueber denselben Ursprung (WebViewAssetLoader). */
private const val EDITOR_HOST = "appassets.androidplatform.net"
private const val EDITOR_URL = "https://$EDITOR_HOST/assets/notizi-editor.html"
private fun blobUrl(name: String) = "https://$EDITOR_HOST/blob/" + android.net.Uri.encode(name)

private fun b64(s: String) = android.util.Base64.encodeToString(s.toByteArray(Charsets.UTF_8), android.util.Base64.NO_WRAP)
private fun unb64(s: String) = String(android.util.Base64.decode(s, android.util.Base64.DEFAULT), Charsets.UTF_8)

/** Bildtyp aus den ersten Bytes - wie Sniff auf dem Server. */
private fun sniff(d: ByteArray): String = when {
    d.size > 4 && d[0] == 0x89.toByte() && d[1] == 0x50.toByte() -> "image/png"
    d.size > 3 && d[0] == 0xFF.toByte() && d[1] == 0xD8.toByte() -> "image/jpeg"
    d.size > 4 && d[0] == 0x47.toByte() && d[1] == 0x49.toByte() -> "image/gif"
    d.size > 12 && d[8] == 0x57.toByte() && d[9] == 0x45.toByte() -> "image/webp"
    d.size > 2 && d[0] == 0x42.toByte() && d[1] == 0x4D.toByte() -> "image/bmp"
    else -> "application/octet-stream"
}

/** Leer: kein Text und kein Bild in den Bloecken - dann gleich mit Tastatur oeffnen. */
private fun isEmptyNote(d: NoteDetail) = d.blocks.all { b ->
    !b.contains("<img", true) && !b.contains("<table", true) &&
        android.text.Html.fromHtml(b, android.text.Html.FROM_HTML_MODE_COMPACT).toString().isBlank()
}

/**
 * Eine Notiz im Editor von notizi (assets/notizi-editor.html): Aufgaben, Listen, Formatierung
 * direkt im Text, Bilder, Zeichnen. Gesperrte Bloecke bleiben gesperrt; unveraenderte Bloecke gehen
 * als ihre Nummer zurueck an den Server. Gespeichert wird beim Zurueck, alle paar Sekunden Ruhe
 * und wenn die App in den Hintergrund geht. Hat ein anderes Geraet die Notiz inzwischen geaendert,
 * wird zusammengefuehrt; geht das nicht, wird die eigene Version als eigene Notiz gesichert.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NoteScreen(id: Long, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val json = remember { Json { ignoreUnknownKeys = true } }
    var note by remember { mutableStateOf<NoteDetail?>(null) }
    var offline by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var web by remember { mutableStateOf<WebView?>(null) }
    var pageReady by remember { mutableStateOf(false) }
    var shown by remember { mutableStateOf(false) }          // Inhalt steht im Editor
    var saving by remember { mutableStateOf(false) }
    var drawing by remember { mutableStateOf(false) }
    var closing by remember { mutableStateOf(false) }
    var menu by remember { mutableStateOf(false) }
    var dialog by remember { mutableStateOf<String?>(null) }  // color, move, rename, delete
    var failed by remember { mutableStateOf<String?>(null) }  // Speichern beim Schliessen misslungen
    var info by remember { mutableStateOf<String?>(null) }    // Hinweis (Konfliktkopie)
    var closeAfterInfo by remember { mutableStateOf(false) }
    val mutex = remember { Mutex() }
    val dark = MaterialTheme.colorScheme.surface.luminance() < 0.5f
    val folders by remember { app.notes.folders() }.collectAsState(initial = emptyList())

    val d0 = note
    val readOnly = d0 == null || d0.readOnly || d0.archived != null || d0.deleted != null

    suspend fun js(script: String): String? {
        val w = web ?: return null
        return suspendCancellableCoroutine { c -> w.evaluateJavascript(script) { c.resumeWith(Result.success(it)) } }
    }
    fun exec(script: String) { web?.evaluateJavascript(script, null) }

    LaunchedEffect(id) {
        // Offline Gespeichertes zuerst abgleichen; klappt das nicht, bleibt die eigene Fassung im Editor
        if (app.notes.hasPending(id)) {
            app.notes.flushPending().firstOrNull()?.let { info = ctx.getString(R.string.note_conflict_copy, it) }
            if (app.notes.hasPending(id)) { note = try { app.notes.load(id, false) } catch (e: Exception) { error = e.message; null }; offline = true; return@LaunchedEffect }
        }
        note = try { app.notes.load(id, true).also { offline = false } } catch (e: Exception) {
            try { app.notes.load(id, false).also { offline = true } } catch (_: Exception) { error = e.message; null }
        }
    }

    /** Notiz in den Editor; caret: Schreibmarke und Ansicht von vorher (caretInfo). */
    suspend fun show(d: NoteDetail, caret: String?) {
        val names = Regex("src=\"blob:([^\"]+)\"").findAll(d.blocks.joinToString("")).map { it.groupValues[1] }.toSet()
        val images = JsonObject(names.associateWith { JsonPrimitive(blobUrl(it)) })
        val ro = d.readOnly || d.archived != null || d.deleted != null
        val payload = buildJsonObject {
            put("blocks", JsonArray(d.blocks.map { JsonPrimitive(it) }))
            put("protected", JsonArray(d.blocks.indices.map { JsonPrimitive(d.protected.getOrNull(it) ?: true) }))
            put("images", images)
            put("dark", dark)
            put("readOnly", ro)
        }
        js("load('${b64(payload.toString())}')")
        if (caret != null) js("restoreCaret('$caret')")
        shown = true
        // Neue (leere) Notiz: Schreibmarke hinein und Tastatur auf
        if (!ro && caret == null && isEmptyNote(d)) {
            js("ensureCaret()")
            val w = web ?: return
            w.requestFocus()
            w.postDelayed({
                (ctx.getSystemService(android.content.Context.INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager)
                    .showSoftInput(w, android.view.inputmethod.InputMethodManager.SHOW_IMPLICIT)
            }, 200)
        }
    }
    LaunchedEffect(pageReady, note != null) {
        val d = note ?: return@LaunchedEffect
        if (pageReady && !shown) show(d, null)
    }

    /** Neu laden, Schreibmarke behalten. */
    suspend fun reshow(d: NoteDetail) { show(d, js("caretInfo()")?.trim('"')) }

    /**
     * Inhalt aus dem Editor holen und speichern, wenn sich etwas geaendert hat. keepEditing: der
     * Editor bleibt offen - die Bloecke bekommen ihre neuen Nummern. Ergebnis null: gesichert,
     * sonst die Fehlermeldung.
     */
    suspend fun save(keepEditing: Boolean): String? = mutex.withLock {
        val d = note ?: return@withLock null
        if (readOnly || !shown || drawing) return@withLock null
        val raw = js("getContent()")?.trim('"')
        if (raw.isNullOrEmpty() || raw == "null") return@withLock ctx.getString(R.string.error_generic, "editor")
        val content = json.parseToJsonElement(unb64(raw)).jsonObject
        val blocks = content["blocks"]!!.jsonArray
        val plain = content["plain"]?.jsonPrimitive?.contentOrNull ?: ""
        fun JsonObject.index() = this["i"]?.jsonPrimitive?.intOrNull
        val unchanged = blocks.size == d.blocks.size && blocks.withIndex().all { (k, b) ->
            val o = b.jsonObject
            o.index() == k && (o["swaps"] as? JsonArray).isNullOrEmpty() && o["align"] == null
        }
        if (unchanged) return@withLock null
        /** Ohne Netz: lokal ablegen, der Abgleich bringt es spaeter zum Server. false: geht offline nicht. */
        suspend fun keepOffline(): Boolean {
            val local = app.notes.saveOffline(d, blocks, plain) ?: return false
            note = local
            offline = true
            error = null
            if (keepEditing) {
                val payload = buildJsonObject {
                    put("count", local.blocks.size)
                    put("protected", JsonArray(local.protected.map { JsonPrimitive(it) }))
                }
                if (js("reindex('${b64(payload.toString())}')") != "true") reshow(local)
            }
            at.dimes.dmoffice.sync.SyncWorker.now(ctx)
            return true
        }
        // Liegt schon eine offline gespeicherte Fassung vor, beziehen sich die Blocknummern auf sie - also dazu legen
        if (app.notes.hasPending(id)) return@withLock if (keepOffline()) null else ctx.getString(R.string.note_offline_images)
        saving = true
        try {
            var base = d
            var send: JsonArray = blocks
            var merged = false
            repeat(3) {
                try {
                    val r = app.notes.save(id, base.version, send, plain)
                    val fresh = app.notes.load(id, true)
                    note = fresh
                    offline = false
                    error = null
                    if (keepEditing) {
                        // Ohne Neuladen weiter, wenn die neue Version genau die gespeicherte ist
                        val payload = buildJsonObject {
                            put("count", fresh.blocks.size)
                            put("protected", JsonArray(fresh.blocks.indices.map { JsonPrimitive(fresh.protected.getOrNull(it) ?: true) }))
                        }
                        val ok = !merged && fresh.version == r.version && js("reindex('${b64(payload.toString())}')") == "true"
                        if (!ok) reshow(fresh)
                    }
                    if (merged) android.widget.Toast.makeText(ctx, R.string.note_merged, android.widget.Toast.LENGTH_SHORT).show()
                    return@withLock null
                } catch (e: ApiException) {
                    if (e.code != 409) throw e
                    // Inzwischen anderswo geaendert: dreiseitig zusammenfuehren
                    val theirs = app.notes.load(id, true)
                    val mine = send.map { b -> b.jsonObject.index()?.let { base.blocks[it] } ?: (b.jsonObject["html"]?.jsonPrimitive?.contentOrNull ?: "") }
                    val result = NoteMerge.diff3(base.blocks, mine, theirs.blocks)
                    if (result == null) {
                        // Dieselbe Stelle verschieden geaendert: eigene Version als neue Notiz sichern
                        val title = ctx.getString(R.string.note_conflict_title, base.title.ifBlank { ctx.getString(R.string.note_untitled) })
                        val copy = app.notes.create(base.folder)
                        val cd = app.notes.load(copy, true)
                        app.notes.save(copy, cd.version, buildJsonArray { mine.forEach { add(buildJsonObject { put("html", it) }) } }, plain)
                        app.notes.setMeta(copy, NoteMeta(customTitle = title))
                        note = theirs
                        if (keepEditing) show(theirs, null)
                        info = ctx.getString(R.string.note_conflict_copy, title)
                        return@withLock null
                    }
                    // Was es in der neuen Version schon gibt, geht als deren Nummer
                    val used = HashSet<Int>()
                    send = buildJsonArray {
                        for (s in result) {
                            val k = theirs.blocks.indices.firstOrNull { it !in used && theirs.blocks[it] == s }
                            if (k != null) { used += k; add(buildJsonObject { put("i", k) }) } else add(buildJsonObject { put("html", s) })
                        }
                    }
                    base = theirs
                    note = theirs
                    merged = true
                }
            }
            ctx.getString(R.string.note_conflict)
        } catch (e: Exception) {
            if (app.notes.isOffline(e) && keepOffline()) return@withLock null
            e.message ?: e.javaClass.simpleName
        } finally { saving = false }
    }

    fun saveAndClose() {
        if (closing) return
        closing = true
        scope.launch {
            val err = save(false)
            closing = false
            // Konfliktkopie angelegt: erst den Hinweis zeigen, OK schliesst dann
            if (err != null) failed = err else if (info != null) closeAfterInfo = true else onClose()
        }
    }
    BackHandler(enabled = !drawing) { saveAndClose() }

    // Alle paar Sekunden Ruhe beim Tippen: sichern (wie notizi alle 30 s)
    LaunchedEffect(shown) {
        if (!shown) return@LaunchedEffect
        while (true) {
            delay(8_000)
            if (readOnly || drawing || closing) continue
            val idle = js("idleSeconds()")?.toDoubleOrNull() ?: 0.0
            if (idle in 2.0..60.0) save(true)?.let { error = ctx.getString(R.string.note_save_failed, it) }
        }
    }
    // In den Hintergrund: sichern
    val owner = LocalLifecycleOwner.current
    DisposableEffect(owner) {
        val obs = LifecycleEventObserver { _, e -> if (e == Lifecycle.Event.ON_PAUSE && !closing) scope.launch { save(true) } }
        owner.lifecycle.addObserver(obs)
        onDispose { owner.lifecycle.removeObserver(obs) }
    }

    /** Titel, Farbe, Wichtig, Ordner aendern; der Text im Editor bleibt, wie er ist. */
    fun meta(m: NoteMeta) = scope.launch {
        try {
            app.notes.setMeta(id, m)
            val fresh = app.notes.load(id, true)
            // Gleicher Text (Version), nur andere Angaben - den Editor nicht anfassen
            note = if (fresh.version == note?.version) fresh else note?.copy(title = fresh.title, customTitle = fresh.customTitle, color = fresh.color, important = fresh.important, folder = fresh.folder)
        } catch (e: Exception) { error = e.message }
    }
    fun act(action: String, close: Boolean) = scope.launch {
        try {
            if (action != "restore") save(false)?.let { failed = it; return@launch }
            if (action == "delete") app.notes.deleteForever(id) else app.notes.act(id, action)
            if (close) onClose() else {
                val fresh = app.notes.load(id, true)
                note = fresh
                show(fresh, null)
            }
        } catch (e: Exception) { error = e.message }
    }

    fun openAttachment(name: String) = scope.launch {
        val a = note?.attachments?.firstOrNull { it.name == name } ?: note?.attachments?.firstOrNull { it.name.equals(name, true) }
        if (a == null) { error = name; return@launch }
        try {
            val bytes = app.notes.blobAsync(a.blob)
            val f = withContext(Dispatchers.IO) {
                val dir = File(ctx.cacheDir, "attachments").apply { mkdirs() }
                File(dir, a.name.ifBlank { "anhang" }.replace(Regex("[^A-Za-z0-9._ -]"), "_")).apply { writeBytes(bytes) }
            }
            val uri = FileProvider.getUriForFile(ctx, ctx.packageName + ".files", f)
            val i = Intent(Intent.ACTION_VIEW).setDataAndType(uri, a.mime.ifBlank { "*/*" }.substringBefore(';')).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
            ctx.startActivity(Intent.createChooser(i, a.name).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        } catch (e: Exception) { error = e.message }
    }

    /** Fertige Zeichnung oder eingefuegtes Bild: hochladen, der Editor setzt es mit seinem Blobnamen ein. */
    fun takeDrawing() = scope.launch {
        drawing = false
        try {
            val raw = js("takeDrawing()")?.trim('"')
            if (raw.isNullOrEmpty() || raw == "null") return@launch
            val d = json.parseToJsonElement(unb64(raw)).jsonObject
            val bytes = android.util.Base64.decode(d["png"]!!.jsonPrimitive.content, android.util.Base64.DEFAULT)
            val name = app.notes.uploadImage(bytes, sniff(bytes).takeIf { it.startsWith("image/") } ?: "image/png")
            js("finishDrawing('$name')")
        } catch (e: Exception) { error = ctx.getString(R.string.note_picture_failed, e.message ?: "") }
    }

    val pickPicture = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch {
            try {
                val jpeg = withContext(Dispatchers.IO) { uprightJpeg(ctx, uri) }
                js("insertPicture('${android.util.Base64.encodeToString(jpeg, android.util.Base64.NO_WRAP)}','image/jpeg')")
            } catch (e: Exception) { error = ctx.getString(R.string.note_picture_failed, e.message ?: "") }
        }
    }

    Scaffold(topBar = {
        TopAppBar(
            title = {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    val d = note
                    if (d != null) Box(Modifier.size(20.dp).background(noteColor(d.color), CircleShape).border(1.dp, MaterialTheme.colorScheme.outline, CircleShape)
                        .clickable(enabled = !readOnly) { dialog = "color" })
                    Text(d?.title?.ifBlank { stringResource(R.string.note_untitled) } ?: "", maxLines = 1, overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(start = 10.dp).clickable(enabled = !readOnly) { dialog = "rename" })
                }
            },
            navigationIcon = { IconButton(onClick = { saveAndClose() }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                val d = note
                if (d != null && !drawing) {
                    if (!readOnly) {
                        IconButton(onClick = { exec("undo()") }) { Icon(Icons.AutoMirrored.Filled.Undo, contentDescription = stringResource(R.string.note_undo)) }
                        IconButton(onClick = { exec("redo()") }) { Icon(Icons.AutoMirrored.Filled.Redo, contentDescription = stringResource(R.string.note_redo)) }
                        IconButton(onClick = { meta(NoteMeta(important = !d.important)) }) {
                            if (d.important) Icon(Icons.Default.Star, contentDescription = stringResource(R.string.note_unstar), tint = androidx.compose.ui.graphics.Color(0xFFF5B400))
                            else Icon(Icons.Default.StarBorder, contentDescription = stringResource(R.string.note_star))
                        }
                    }
                    Box {
                        IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.note_more)) }
                        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                            @Composable fun item(text: Int, icon: androidx.compose.ui.graphics.vector.ImageVector, onClick: () -> Unit) =
                                DropdownMenuItem(text = { Text(stringResource(text)) }, leadingIcon = { Icon(icon, contentDescription = null) }, onClick = { menu = false; onClick() })
                            if (!readOnly) {
                                item(R.string.note_insert_picture, Icons.Default.Image) { pickPicture.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) }
                                item(R.string.note_color, Icons.Default.Palette) { dialog = "color" }
                                item(R.string.note_move, Icons.Default.Folder) { dialog = "move" }
                                item(R.string.note_rename, Icons.Default.Edit) { dialog = "rename" }
                                HorizontalDivider()
                                item(R.string.archive, Icons.Default.Archive) { act("archive", true) }
                                item(R.string.note_trash, Icons.Default.Delete) { act("trash", true) }
                            } else if (d.archived != null || d.deleted != null) {
                                item(R.string.note_restore, Icons.Default.Restore) { act("restore", false) }
                                if (d.deleted != null) item(R.string.note_delete_forever, Icons.Default.DeleteForever) { dialog = "delete" }
                            }
                        }
                    }
                }
            }
        )
    }, bottomBar = {
        if (note != null && !readOnly && !drawing) Surface(tonalElevation = 3.dp, modifier = Modifier.windowInsetsPadding(WindowInsets.ime.union(WindowInsets.navigationBars))) {
            Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 4.dp)) {
                IconButton(onClick = { exec("toggleTask()") }) { Icon(Icons.Default.CheckBox, contentDescription = stringResource(R.string.note_task)) }
                IconButton(onClick = { exec("cmd('insertUnorderedList')") }) { Icon(Icons.AutoMirrored.Filled.FormatListBulleted, contentDescription = stringResource(R.string.note_list)) }
                IconButton(onClick = { exec("cmd('bold')") }) { Icon(Icons.Default.FormatBold, contentDescription = stringResource(R.string.note_bold)) }
                IconButton(onClick = { exec("cmd('italic')") }) { Icon(Icons.Default.FormatItalic, contentDescription = stringResource(R.string.note_italic)) }
                IconButton(onClick = { exec("cmd('underline')") }) { Icon(Icons.Default.FormatUnderlined, contentDescription = stringResource(R.string.note_underline)) }
                IconButton(onClick = { exec("cmd('strikeThrough')") }) { Icon(Icons.Default.FormatStrikethrough, contentDescription = stringResource(R.string.note_strike)) }
                IconButton(onClick = { exec("toggleCode()") }) { Icon(Icons.Default.Code, contentDescription = stringResource(R.string.note_code)) }
                IconButton(onClick = { pickPicture.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) }) { Icon(Icons.Default.Image, contentDescription = stringResource(R.string.note_insert_picture)) }
            }
        }
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            if (saving) LinearProgressIndicator(Modifier.fillMaxWidth())
            error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) }
            val d = note
            if (d == null) { if (error == null) CircularProgressIndicator(Modifier.padding(24.dp)); return@Column }
            val hint = when {
                d.deleted != null -> R.string.note_trashed
                d.archived != null -> R.string.note_archived
                d.readOnly -> R.string.note_read_only
                offline -> R.string.note_offline
                else -> null
            }
            if (hint != null) Text(stringResource(hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
            if (d.attachments.isNotEmpty()) Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                for (a in d.attachments) AssistChip(onClick = { openAttachment(a.name) }, label = { Text(a.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    leadingIcon = { Icon(Icons.Default.AttachFile, contentDescription = stringResource(R.string.note_attachment), Modifier.size(18.dp)) })
            }
            AndroidView(
                factory = { c ->
                    // Seite aus den Assets, Bilder der Notiz vom Server - beides ueber denselben Ursprung
                    val loader = WebViewAssetLoader.Builder().setDomain(EDITOR_HOST)
                        .addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(c))
                        .addPathHandler("/blob/") { path ->
                            try {
                                val bytes = app.notes.blob(android.net.Uri.decode(path))
                                WebResourceResponse(sniff(bytes), null, ByteArrayInputStream(bytes))
                            } catch (e: Exception) {
                                WebResourceResponse("text/plain", "utf-8", 404, "Not Found", emptyMap(), ByteArrayInputStream(ByteArray(0)))
                            }
                        }.build()
                    WebView(c).apply {
                        settings.javaScriptEnabled = true
                        settings.domStorageEnabled = true
                        settings.builtInZoomControls = true
                        settings.displayZoomControls = false
                        settings.allowFileAccess = false
                        setBackgroundColor(if (dark) 0xFF1E1E1E.toInt() else 0xFFFFFFFF.toInt())
                        webViewClient = object : WebViewClient() {
                            override fun shouldInterceptRequest(view: WebView?, request: WebResourceRequest): WebResourceResponse? =
                                loader.shouldInterceptRequest(request.url)
                            override fun onPageFinished(view: WebView?, url: String?) { pageReady = true }
                            override fun shouldOverrideUrlLoading(view: WebView?, request: WebResourceRequest?): Boolean {
                                val u = request?.url?.toString() ?: return false
                                when {
                                    u.startsWith("notizi://back") -> saveAndClose()
                                    u.startsWith("notizi://drawing") -> takeDrawing()
                                    u.startsWith("notizi://drawmode/") -> drawing = u.endsWith("/on")
                                    u.startsWith("notizi://attachment/") -> openAttachment(android.net.Uri.decode(u.removePrefix("notizi://attachment/")))
                                    u.startsWith("notizi://copy") -> scope.launch {
                                        val raw = js("takeCopy()")?.trim('"') ?: return@launch
                                        val cm = ctx.getSystemService(android.content.Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager
                                        cm.setPrimaryClip(android.content.ClipData.newPlainText("code", unb64(raw)))
                                    }
                                    u.startsWith("https://$EDITOR_HOST/") -> return false
                                    // Links in der Notiz: im Browser
                                    u.startsWith("http://") || u.startsWith("https://") || u.startsWith("mailto:") ->
                                        try { ctx.startActivity(Intent(Intent.ACTION_VIEW, android.net.Uri.parse(u)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) } catch (_: Exception) { }
                                }
                                return true
                            }
                        }
                        loadUrl(EDITOR_URL)
                        web = this
                    }
                },
                modifier = Modifier.fillMaxSize()
            )
        }
    }

    // ---------------- Dialoge ----------------
    val d = note
    when (dialog) {
        "color" -> if (d != null) AlertDialog(onDismissRequest = { dialog = null }, title = { Text(stringResource(R.string.note_color)) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    for (row in NOTE_COLORS.chunked(4)) Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                        for (hex in row) Box(Modifier.size(40.dp).background(noteColor(hex), CircleShape)
                            .border(if (hex.equals(d.color, true)) 3.dp else 1.dp, if (hex.equals(d.color, true)) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline, CircleShape)
                            .clickable { dialog = null; meta(NoteMeta(color = hex)) })
                    }
                }
            },
            confirmButton = { TextButton(onClick = { dialog = null }) { Text(stringResource(R.string.cancel)) } })
        "move" -> if (d != null) AlertDialog(onDismissRequest = { dialog = null }, title = { Text(stringResource(R.string.note_move)) },
            text = {
                // Ordner als Baum, eingerueckt nach Tiefe
                fun depth(f: at.dimes.dmoffice.api.NoteFolder): Int { var n = 0; var p = f.parent; while (p != null && n < 10) { n++; p = folders.firstOrNull { it.syncId == p }?.parent }; return n }
                fun tree(parent: String?): List<at.dimes.dmoffice.api.NoteFolder> = folders.filter { it.parent == parent }.flatMap { listOf(it) + tree(it.syncId) }
                LazyColumn {
                    item { ListItem(headlineContent = { Text(stringResource(R.string.note_top_level)) }, modifier = Modifier.clickable { dialog = null; meta(NoteMeta(folder = "")) }) }
                    items(tree(null)) { f ->
                        ListItem(headlineContent = { Text(f.name) }, leadingContent = { Icon(Icons.Default.Folder, contentDescription = null) },
                            modifier = Modifier.padding(start = (depth(f) * 16).dp).clickable { dialog = null; meta(NoteMeta(folder = f.syncId)) },
                            colors = if (f.syncId == d.folder) androidx.compose.material3.ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.secondaryContainer) else androidx.compose.material3.ListItemDefaults.colors())
                    }
                }
            },
            confirmButton = { TextButton(onClick = { dialog = null }) { Text(stringResource(R.string.cancel)) } })
        "rename" -> if (d != null) {
            var t by remember { mutableStateOf(d.customTitle ?: d.title) }
            AlertDialog(onDismissRequest = { dialog = null }, title = { Text(stringResource(R.string.note_rename)) },
                text = { OutlinedTextField(t, { t = it }, singleLine = true, placeholder = { Text(stringResource(R.string.note_title_hint)) }) },
                confirmButton = {
                    TextButton(onClick = {
                        dialog = null
                        // Erst den Text sichern - wie in notizi
                        scope.launch { save(true); meta(if (t.isBlank()) NoteMeta(clearCustomTitle = true) else NoteMeta(customTitle = t.trim())) }
                    }) { Text(stringResource(R.string.save)) }
                },
                dismissButton = { TextButton(onClick = { dialog = null }) { Text(stringResource(R.string.cancel)) } })
        }
        "delete" -> AlertDialog(onDismissRequest = { dialog = null }, text = { Text(stringResource(R.string.note_delete_forever_q)) },
            confirmButton = { TextButton(onClick = { dialog = null; act("delete", true) }) { Text(stringResource(R.string.note_delete_forever), color = MaterialTheme.colorScheme.error) } },
            dismissButton = { TextButton(onClick = { dialog = null }) { Text(stringResource(R.string.cancel)) } })
    }
    failed?.let { msg ->
        AlertDialog(onDismissRequest = { failed = null }, text = { Text(stringResource(R.string.note_save_failed, msg)) },
            confirmButton = { TextButton(onClick = { failed = null }) { Text(stringResource(R.string.note_keep_editing)) } },
            dismissButton = { TextButton(onClick = { failed = null; onClose() }) { Text(stringResource(R.string.note_discard), color = MaterialTheme.colorScheme.error) } })
    }
    info?.let { msg ->
        AlertDialog(onDismissRequest = { info = null; if (closeAfterInfo) onClose() }, text = { Text(msg) },
            confirmButton = { TextButton(onClick = { info = null; if (closeAfterInfo) onClose() }) { Text("OK") } })
    }
}

/**
 * Foto aus der Auswahl: aufrecht gedreht (der PC liest die Drehung aus den Bilddaten nicht) und
 * auf hoechstens 2000 Pixel verkleinert, als JPEG.
 */
private fun uprightJpeg(ctx: android.content.Context, uri: android.net.Uri): ByteArray {
    val cr = ctx.contentResolver
    val bounds = android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds = true }
    cr.openInputStream(uri)!!.use { android.graphics.BitmapFactory.decodeStream(it, null, bounds) }
    var sample = 1
    while (maxOf(bounds.outWidth, bounds.outHeight) / (sample * 2) >= 2000) sample *= 2
    val bmp = cr.openInputStream(uri)!!.use { android.graphics.BitmapFactory.decodeStream(it, null, android.graphics.BitmapFactory.Options().apply { inSampleSize = sample }) }
        ?: throw IllegalArgumentException("Bild")
    val rotation = try {
        cr.openInputStream(uri)!!.use {
            when (android.media.ExifInterface(it).getAttributeInt(android.media.ExifInterface.TAG_ORIENTATION, 1)) {
                android.media.ExifInterface.ORIENTATION_ROTATE_90 -> 90f
                android.media.ExifInterface.ORIENTATION_ROTATE_180 -> 180f
                android.media.ExifInterface.ORIENTATION_ROTATE_270 -> 270f
                else -> 0f
            }
        }
    } catch (_: Exception) { 0f }
    val scale = minOf(1f, 2000f / maxOf(bmp.width, bmp.height))
    val m = android.graphics.Matrix().apply { postScale(scale, scale); postRotate(rotation) }
    val out = android.graphics.Bitmap.createBitmap(bmp, 0, 0, bmp.width, bmp.height, m, true)
    return java.io.ByteArrayOutputStream().use { s -> out.compress(android.graphics.Bitmap.CompressFormat.JPEG, 85, s); s.toByteArray() }
}
