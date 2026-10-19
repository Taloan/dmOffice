package at.dimes.dmoffice.ui

import android.webkit.WebView
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.background
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Code
import androidx.compose.material.icons.filled.FormatStrikethrough
import androidx.compose.material.icons.filled.FormatItalic
import androidx.compose.material.icons.filled.FormatBold
import androidx.compose.material.icons.filled.CheckBox
import androidx.compose.material.icons.automirrored.filled.FormatListBulleted
import androidx.compose.material.icons.automirrored.filled.Redo
import androidx.compose.material.icons.automirrored.filled.Undo
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.webkit.WebSettingsCompat
import androidx.webkit.WebViewFeature
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.ApiException
import at.dimes.dmoffice.api.NoteDetail
import kotlinx.coroutines.launch

/** Notizenliste mit Suche; die Liste kommt aus dem Cache und wird beim Oeffnen aktualisiert. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NotesScreen(onOpen: (Long) -> Unit, onClose: (() -> Unit)?) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var q by remember { mutableStateOf("") }
    val list by remember(q) { app.notes.search(q) }.collectAsState(initial = emptyList())
    var error by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(false) }

    suspend fun refresh() { loading = true; try { app.notes.refresh() } catch (e: Exception) { error = e.message } finally { loading = false } }
    LaunchedEffect(Unit) { refresh() }

    Scaffold(
        topBar = {
            TopAppBar(title = { Text(stringResource(R.string.notes_tab)) },
                // Als Reiter ohne Zurueck-Pfeil
                navigationIcon = { if (onClose != null) IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
                actions = { IconButton(onClick = { scope.launch { refresh() } }) { Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.refresh)) } })
        },
        floatingActionButton = {
            FloatingActionButton(onClick = { scope.launch { try { onOpen(app.notes.create(null)) } catch (e: Exception) { error = e.message } } }) { Icon(Icons.Default.Add, contentDescription = stringResource(R.string.new_note)) }
        }
    ) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            OutlinedTextField(value = q, onValueChange = { q = it }, placeholder = { Text(stringResource(R.string.search)) }, singleLine = true, modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp))
            error?.let { Text(stringResource(R.string.error_generic, it), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp)) }
            if (loading && list.isEmpty()) CircularProgressIndicator(Modifier.padding(24.dp))
            if (list.isEmpty() && !loading) Text(stringResource(R.string.no_notes), modifier = Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
                items(list.size, key = { i -> list[i].id }) { i ->
                    val n = list[i]
                    ListItem(
                        // Senkrechter Balken in der Farbe der Notiz (wie im Web)
                        leadingContent = {
                            androidx.compose.foundation.layout.Box(Modifier.width(4.dp).height(40.dp)
                                .background(noteColor(n.color), androidx.compose.foundation.shape.RoundedCornerShape(2.dp)))
                        },
                        headlineContent = { Text(n.title.ifBlank { "–" }, fontWeight = FontWeight.Medium, maxLines = 1) },
                        supportingContent = { Text(n.snippet, maxLines = 2) },
                        trailingContent = { if (n.important) Icon(Icons.Default.Star, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                        modifier = Modifier.clickable { onOpen(n.id) }
                    )
                }
            }
        }
    }
}

/**
 * Eine Notiz im Editor von Notizi (assets/notizi-editor.html): Aufgaben, Listen, Formatierung direkt im Text.
 * Gesperrte Bloecke bleiben gesperrt; unveraenderte Bloecke gehen als ihr Original zurueck an den Server.
 * Gespeichert wird beim Haken oben und beim Zurueck.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NoteScreen(id: Long, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var note by remember { mutableStateOf<NoteDetail?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var web by remember { mutableStateOf<WebView?>(null) }
    var pageReady by remember { mutableStateOf(false) }
    var loadedVersion by remember { mutableStateOf<String?>(null) }
    var saving by remember { mutableStateOf(false) }
    val dark = isSystemInDarkTheme()

    LaunchedEffect(id) {
        note = try { app.notes.load(id, true) } catch (e: Exception) {
            try { app.notes.load(id, false) } catch (e2: Exception) { error = e2.message; null }
        }
    }
    // Inhalt in den Editor, sobald Seite und Notiz da sind (und nach einem Neuladen wegen Konflikt)
    LaunchedEffect(pageReady, note?.version) {
        val d = note ?: return@LaunchedEffect
        val w = web ?: return@LaunchedEffect
        if (!pageReady || loadedVersion == d.version) return@LaunchedEffect
        val payload = org.json.JSONObject()
            .put("blocks", org.json.JSONArray(d.blocks))
            .put("protected", org.json.JSONArray(d.blocks.indices.map { d.protected.getOrNull(it) ?: false }))
            .put("images", org.json.JSONObject())
            .put("dark", dark)
            .put("readOnly", d.readOnly)
        val b64 = android.util.Base64.encodeToString(payload.toString().toByteArray(Charsets.UTF_8), android.util.Base64.NO_WRAP)
        w.evaluateJavascript("load('$b64')", null)
        loadedVersion = d.version
    }

    /** Aus dem Editor holen und speichern, wenn sich etwas geaendert hat. true: alles gesichert. */
    suspend fun save(): Boolean {
        val d = note ?: return true
        val w = web ?: return true
        if (d.readOnly || loadedVersion == null) return true
        val raw = kotlinx.coroutines.suspendCancellableCoroutine<String?> { c -> w.evaluateJavascript("getContent()") { c.resumeWith(Result.success(it)) } }
            ?.trim('"') ?: return false
        if (raw.isEmpty() || raw == "null") return false
        val json = org.json.JSONObject(String(android.util.Base64.decode(raw, android.util.Base64.DEFAULT), Charsets.UTF_8))
        val arr = json.getJSONArray("blocks")
        val blocks = (0 until arr.length()).map { k ->
            val b = arr.getJSONObject(k)
            if (b.has("i")) b.getInt("i") to null else null to b.optString("html")
        }
        val unchanged = blocks.size == d.blocks.size && blocks.withIndex().all { (k, b) -> b.first == k }
        if (unchanged) return true
        saving = true
        return try {
            app.notes.save(id, d.version, blocks, json.optString("plain"))
            note = app.notes.load(id, true)
            loadedVersion = note?.version
            true
        } catch (e: ApiException) {
            if (e.code == 409) { error = ctx.getString(R.string.note_conflict); note = app.notes.load(id, true) } else error = e.message
            false
        } catch (e: Exception) { error = e.message; false } finally { saving = false }
    }
    fun saveAndClose() { scope.launch { if (save()) onClose() } }
    androidx.activity.compose.BackHandler { saveAndClose() }
    fun exec(js: String) { web?.evaluateJavascript(js, null) }

    Scaffold(topBar = {
        TopAppBar(
            title = { Text(note?.title ?: "", maxLines = 1) },
            navigationIcon = { IconButton(onClick = { saveAndClose() }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                val d = note
                if (d != null && !d.readOnly) {
                    IconButton(onClick = { exec("undo()") }) { Icon(Icons.AutoMirrored.Filled.Undo, contentDescription = null) }
                    IconButton(onClick = { exec("redo()") }) { Icon(Icons.AutoMirrored.Filled.Redo, contentDescription = null) }
                    IconButton(enabled = !saving, onClick = {
                        scope.launch { if (save()) android.widget.Toast.makeText(ctx, ctx.getString(R.string.saved), android.widget.Toast.LENGTH_SHORT).show() }
                    }) { Icon(Icons.Default.Check, contentDescription = stringResource(R.string.save)) }
                }
            }
        )
    }, bottomBar = {
        val d = note
        if (d != null && !d.readOnly) androidx.compose.material3.BottomAppBar {
            IconButton(onClick = { exec("toggleTask()") }) { Icon(Icons.Default.CheckBox, contentDescription = null) }
            IconButton(onClick = { exec("cmd('insertUnorderedList')") }) { Icon(Icons.AutoMirrored.Filled.FormatListBulleted, contentDescription = null) }
            IconButton(onClick = { exec("cmd('bold')") }) { Icon(Icons.Default.FormatBold, contentDescription = null) }
            IconButton(onClick = { exec("cmd('italic')") }) { Icon(Icons.Default.FormatItalic, contentDescription = null) }
            IconButton(onClick = { exec("cmd('strikeThrough')") }) { Icon(Icons.Default.FormatStrikethrough, contentDescription = null) }
            IconButton(onClick = { exec("toggleCode()") }) { Icon(Icons.Default.Code, contentDescription = null) }
        }
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            error?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
            val d = note
            if (d == null) { if (error == null) CircularProgressIndicator(Modifier.padding(24.dp)); return@Column }
            if (d.readOnly) Text(stringResource(R.string.note_read_only), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 16.dp))
            AndroidView(
                factory = { c ->
                    WebView(c).apply {
                        settings.javaScriptEnabled = true
                        settings.domStorageEnabled = true
                        settings.builtInZoomControls = true
                        settings.displayZoomControls = false
                        setBackgroundColor(if (dark) 0xFF1E1E1E.toInt() else 0xFFFFFFFF.toInt())
                        webViewClient = object : android.webkit.WebViewClient() {
                            override fun onPageFinished(view: WebView?, url: String?) { pageReady = true }
                            // notizi://... aus dem Editor: Wischen zurueck; Kopieren, Malen und Anhaenge gibt es hier nicht
                            override fun shouldOverrideUrlLoading(view: WebView?, request: android.webkit.WebResourceRequest?): Boolean {
                                val u = request?.url?.toString() ?: return false
                                if (u.startsWith("notizi://back")) saveAndClose()
                                return u.startsWith("notizi://") || !u.startsWith("file:")
                            }
                        }
                        loadUrl("file:///android_asset/notizi-editor.html")
                        web = this
                    }
                },
                modifier = Modifier.fillMaxSize()
            )
        }
    }
}

/** Text eines HTML-Blocks fuer das Bearbeiten: Zeilenumbrueche fuer br/li/p, Tags weg. */
fun blockText(html: String): String =
    android.text.Html.fromHtml(html.replace(Regex("(?i)<br\\s*/?>"), "\n").replace(Regex("(?i)</(p|li|div|h[1-6])>"), "\n"), android.text.Html.FROM_HTML_MODE_COMPACT).toString().trim('\n')

/** Absatz zurueck nach XHTML: Zeilen mit br, alles maskiert. */
fun textToHtml(text: String): String = "<p>" + android.text.Html.escapeHtml(text).replace("\n", "<br />") + "</p>"

/** Farbe einer Notiz (#rrggbb) - ohne Farbe das Gelb von Notizi. */
fun noteColor(hex: String?): androidx.compose.ui.graphics.Color =
    try { androidx.compose.ui.graphics.Color(android.graphics.Color.parseColor(hex ?: "#FFF9A8")) } catch (_: Exception) { androidx.compose.ui.graphics.Color(0xFFFFF9A8) }
