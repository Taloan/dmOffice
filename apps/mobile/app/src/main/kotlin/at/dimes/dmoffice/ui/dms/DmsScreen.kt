package at.dimes.dmoffice.ui.dms

import android.net.Uri
import android.provider.OpenableColumns
import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.automirrored.filled.ViewList
import androidx.compose.material.icons.filled.Cloud
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.GridView
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyGridItemSpanScope
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.DocumentScanner
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.LockOpen
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.UploadFile
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SmallFloatingActionButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.ApiException
import at.dimes.dmoffice.ui.TabTopBar
import at.dimes.dmoffice.ui.TresorGate
import at.dimes.dmoffice.ui.rememberTabSearch
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

/**
 * Bereich „DMS“ der App (docs/dms-konzept.md, Stufe 4): Posteingang, alle Dokumente, virtuelle Bäume, Suche, Tresor;
 * ein Dokument ansehen und seine Angaben bearbeiten; Dateien hochladen und Papier abfotografieren.
 */
@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
@Composable
fun DmsScreen() {
    val ctx = LocalContext.current
    val repo = remember { DmsRepo(ctx) }
    val scope = rememberCoroutineScope()
    val search = rememberTabSearch()
    var view by rememberSaveable { mutableStateOf("inbox") }
    var tresor by rememberSaveable { mutableStateOf(false) }
    var state by remember { mutableStateOf<DmsState?>(null) }
    var entries by remember { mutableStateOf<List<DmsEntry>>(emptyList()) }
    var profiles by remember { mutableStateOf<List<DmsProfile>>(emptyList()) }
    var docs by remember { mutableStateOf<List<DmsDoc>?>(null) }
    var total by remember { mutableIntStateOf(0) }
    var error by remember { mutableStateOf<String?>(null) }
    var tree by rememberSaveable { mutableIntStateOf(0) }
    var nodes by remember { mutableStateOf<List<DmsNode>?>(null) }
    var path by remember { mutableStateOf<List<DmsNode>>(emptyList()) }
    var open by remember { mutableStateOf<DmsDoc?>(null) }
    var reload by remember { mutableIntStateOf(0) }
    var uploading by remember { mutableStateOf(false) }
    // Langer Druck auf einen Eintrag im Baum: Untereintrag, Umbenennen, Verschieben
    var acting by remember { mutableStateOf<DmsEntry?>(null) }
    val prefs = remember { at.dimes.dmoffice.App.get(ctx).prefs }
    var tiles by remember { mutableStateOf(prefs.dmsTiles) }
    // Ablageprofil wählen, wenn es mehrere gibt (Hochladen und Scannen); dann die vorbereiteten Dateien
    var pending by remember { mutableStateOf<List<Triple<File, String, String?>>?>(null) }
    val tresorChanged by TresorGate.changed.collectAsState()

    // Stand (Zähler) auch beim Wechsel des Reiters neu holen
    LaunchedEffect(reload, tresorChanged, view) {
        try {
            state = repo.state()
            entries = repo.lists()
            profiles = repo.profiles()
        } catch (e: Exception) { error = e.message }
    }
    // Liste: Posteingang bzw. alle, mit Suche; im Baum die Dokumente des gewählten Ordners
    LaunchedEffect(view, tresor, search.query, path, reload, tresorChanged) {
        if (view == "trees" && path.isEmpty() && !search.active) { docs = emptyList(); total = 0; return@LaunchedEffect }
        delay(if (search.active) 300 else 0)
        try {
            val f = when {
                view == "trees" && !search.active -> filterOf(path)
                view == "queue" && !search.active -> DmsFilter(queued = true)
                else -> DmsFilter(inbox = true.takeIf { view == "inbox" && !search.active })
            }
            val (items, n) = repo.docs(f.copy(q = search.query.takeIf { search.active }), tresor)
            docs = items; total = n; error = null
        } catch (e: ApiException) { if (e.code == 423) { tresor = false } else error = e.message }
        catch (e: Exception) { error = e.message }
    }
    var nodesOf by remember { mutableStateOf("") }
    LaunchedEffect(view, tree, tresor, reload, tresorChanged) {
        if (view != "trees") return@LaunchedEffect
        // Nur bei anderem Baum leeren, beim Nachladen nicht flackern
        if (nodesOf != "$tree/$tresor") { nodes = null; nodesOf = "$tree/$tresor" }
        try { nodes = repo.tree(dmsTrees[tree].levels, tresor) } catch (e: Exception) { error = e.message }
    }
    // Noch in Arbeit (Texterkennung): alle 4 s Zähler und Liste neu laden, bis die Warteschlange leer ist (angehalten: nicht)
    val waiting = ((if (tresor) state?.tresorQueued else state?.queued) ?: 0) > 0 || docs?.any { it.state == "queued" } == true
    LaunchedEffect(waiting, state?.paused) {
        if (!waiting || state?.paused == true) return@LaunchedEffect
        while (true) { delay(4_000); reload++ }
    }
    // Zurück in die App: neu laden
    val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle
    androidx.compose.runtime.DisposableEffect(lifecycle) {
        var first = true
        val o = androidx.lifecycle.LifecycleEventObserver { _, e ->
            if (e == androidx.lifecycle.Lifecycle.Event.ON_RESUME) { if (first) first = false else reload++ }
        }
        lifecycle.addObserver(o)
        onDispose { lifecycle.removeObserver(o) }
    }

    fun upload(files: List<Triple<File, String, String?>>, profile: String?) {
        uploading = true
        scope.launch {
            var dup = 0; var dropped = 0
            try {
                for ((f, name, mime) in files) {
                    val r = repo.upload(f, name, mime, profile, tresor && profile == null)
                    if (r.duplicate) dup++
                    if (r.dropped) dropped++
                }
                Toast.makeText(ctx, when {
                    dropped > 0 -> ctx.getString(R.string.dms_dropped)
                    dup > 0 -> ctx.getString(R.string.dms_duplicates, dup)
                    else -> ctx.resources.getQuantityString(R.plurals.dms_uploaded, files.size, files.size)
                }, Toast.LENGTH_LONG).show()
                if (view == "trees") reload++ else { view = "inbox"; reload++ }
            } catch (e: Exception) { Toast.makeText(ctx, e.message ?: e.toString(), Toast.LENGTH_LONG).show() }
            finally { files.forEach { it.first.delete() }; uploading = false }
        }
    }
    /** Mit einem Profil direkt, sonst erst fragen; in der Ansicht „Tresor“ immer das Profil mit Ziel Tresor. */
    fun send(files: List<Triple<File, String, String?>>) {
        val usable = profiles.filter { (it.target == "tresor") == tresor }
        if (usable.size > 1) pending = files else upload(files, usable.firstOrNull()?.id)
    }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris: List<Uri> ->
        if (uris.isEmpty()) return@rememberLauncherForActivityResult
        scope.launch {
            val files = withContext(Dispatchers.IO) { uris.mapNotNull { u -> copyToCache(ctx, u) } }
            if (files.isNotEmpty()) send(files)
        }
    }
    val scan = rememberDmsScanner { pdf -> send(listOf(Triple(pdf, pdf.name, "application/pdf"))) }
    var pickStored by remember { mutableStateOf(false) }
    if (pickStored) at.dimes.dmoffice.ui.StoredFilePicker(onDismiss = { pickStored = false }) { picked ->
        pickStored = false
        val paths = picked.filter { !it.isDir }.map { it.path }
        if (paths.isNotEmpty()) scope.launch {
            uploading = true
            try { repo.importFiles(paths, profiles.filter { it.target != "tresor" }.singleOrNull()?.id); view = "inbox"; reload++ }
            catch (e: Exception) { Toast.makeText(ctx, e.message ?: e.toString(), Toast.LENGTH_LONG).show() }
            finally { uploading = false }
        }
    }

    if (scan.Screen()) return
    open?.let { d ->
        DmsDocScreen(d, tresor, state?.tresor == true, entries, repo, onClose = { open = null; reload++ }, onChanged = { open = it },
            onConfirmed = {
                // Nächsten offenen Eintrag zeigen: der nächste unter dem bestätigten, am Ende der vorige; sonst zurück zur Liste
                val list = docs.orEmpty()
                val at = list.indexOfFirst { it.id == d.id }
                val isOpen = { x: DmsDoc -> x.inbox && x.id != d.id }
                open = if (at < 0) null else list.drop(at + 1).firstOrNull(isOpen) ?: list.take(at).lastOrNull(isOpen)
                if (view == "inbox") docs = list.filter { it.id != d.id }
                if (open == null) reload++
            })
        return
    }
    BackHandler(enabled = view == "trees" && path.isNotEmpty()) { path = path.dropLast(1) }

    Box(Modifier.fillMaxSize()) {
        Column(Modifier.fillMaxSize()) {
            TabTopBar(if (tresor) stringResource(R.string.dms_tresor_title) else "DMS", search, searchHint = stringResource(R.string.dms_search),
                insets = WindowInsets(0), actions = {
                    if (state?.tresor == true) IconButton(onClick = {
                        if (tresor) { tresor = false; path = emptyList() }
                        else scope.launch {
                            if (state?.tresorOpen == true || TresorGate.prompt("tresor")) { tresor = true; path = emptyList(); reload++ }
                        }
                    }) { Icon(if (tresor) Icons.Default.LockOpen else Icons.Default.Lock, contentDescription = stringResource(R.string.dms_tresor)) }
                    // Liste oder Kacheln (Vorschau der ersten Seite), gemerkt
                    IconButton(onClick = { tiles = !tiles; prefs.dmsTiles = tiles }) {
                        Icon(if (tiles) Icons.AutoMirrored.Filled.ViewList else Icons.Default.GridView,
                            contentDescription = stringResource(if (tiles) R.string.dms_view_list else R.string.dms_view_tiles))
                    }
                    IconButton(onClick = { reload++ }) { Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.dms_refresh)) }
                })
            if (!search.active) Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                val inbox = if (tresor) state?.tresorInbox else state?.inbox
                FilterChip(view == "inbox", { view = "inbox" }, { Text(stringResource(R.string.dms_inbox) + if ((inbox ?: 0) > 0) " ($inbox)" else "") })
                val queued = (if (tresor) state?.tresorQueued else state?.queued) ?: 0
                if (queued > 0 || state?.paused == true || view == "queue") FilterChip(view == "queue", { view = "queue" },
                    { Text(stringResource(if (state?.paused == true) R.string.dms_queue_paused else R.string.dms_queue) + if (queued > 0) " ($queued)" else "") })
                FilterChip(view == "all", { view = "all" }, { Text(stringResource(R.string.dms_all)) })
                FilterChip(view == "trees", { view = "trees" }, { Text(stringResource(R.string.dms_trees)) })
            }
            if (uploading) LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 4.dp))
            // Warteschlange anhalten: Quellen nehmen weiter an, verarbeitet wird erst nach dem Fortsetzen
            if (view == "queue" && !search.active) Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), horizontalArrangement = Arrangement.End) {
                androidx.compose.material3.OutlinedButton(onClick = { scope.launch { try { repo.pause(state?.paused != true); reload++ } catch (e: Exception) { error = e.message } } }) {
                    Text(stringResource(if (state?.paused == true) R.string.dms_resume else R.string.dms_pause))
                }
            }
            error?.let { Text(stringResource(R.string.error_generic, it), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
            if (state?.ocr == false) Text(stringResource(R.string.dms_no_ocr), style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))

            // Ein Raster für alles: Kacheln nebeneinander, in der Liste eine Spalte; Bäume und Hinweise über die ganze Breite
            val full: LazyGridItemSpanScope.(Any?) -> GridItemSpan = { GridItemSpan(maxLineSpan) }
            LazyVerticalGrid(if (tiles) GridCells.Adaptive(150.dp) else GridCells.Fixed(1), Modifier.fillMaxSize(),
                contentPadding = PaddingValues(start = if (tiles) 8.dp else 0.dp, end = if (tiles) 8.dp else 0.dp, bottom = 140.dp)) {
                if (view == "trees" && !search.active) {
                    item(span = { GridItemSpan(maxLineSpan) }) {
                        Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 4.dp),
                            horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            dmsTrees.forEachIndexed { i, t -> FilterChip(i == tree, { tree = i; path = emptyList() }, { Text(stringResource(t.name)) }) }
                        }
                    }
                    if (path.isNotEmpty()) item(span = { GridItemSpan(maxLineSpan) }) {
                        ListItem(modifier = Modifier.clickable { path = path.dropLast(1) },
                            leadingContent = { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) },
                            headlineContent = { Text(path.joinToString(" › ") { nodeName(ctx, it, entries) }, maxLines = 2, overflow = TextOverflow.Ellipsis) })
                    }
                    val level = if (path.isEmpty()) nodes else path.last().children
                    if (level == null) item(span = { GridItemSpan(maxLineSpan) }) { CircularProgressIndicator(Modifier.padding(24.dp)) }
                    else items(level, key = { "n" + it.level + it.key }, span = full) { n ->
                        val entry = if (n.level == "correspondent" || n.level == "doctype" || n.level == "tag") entries.firstOrNull { it.id == n.key } else null
                        ListItem(modifier = Modifier.combinedClickable(onLongClick = entry?.let { { acting = it } }) { path = path + n },
                            leadingContent = { Icon(Icons.Default.Folder, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                            headlineContent = { Text(nodeName(ctx, n, entries), maxLines = 1, overflow = TextOverflow.Ellipsis) },
                            trailingContent = { Text(n.count.toString(), color = MaterialTheme.colorScheme.onSurfaceVariant) })
                    }
                    if (path.isNotEmpty() && docs?.isNotEmpty() == true) item(span = { GridItemSpan(maxLineSpan) }) { HorizontalDivider(Modifier.padding(vertical = 4.dp)) }
                }
                val list = docs
                if (list == null) { if (view != "trees") item(span = { GridItemSpan(maxLineSpan) }) { CircularProgressIndicator(Modifier.padding(24.dp)) } }
                else {
                    if (list.isEmpty() && (view != "trees" || search.active)) item(span = { GridItemSpan(maxLineSpan) }) {
                        Text(stringResource(when { search.active -> R.string.dms_none; view == "inbox" -> R.string.dms_inbox_empty; view == "queue" -> R.string.dms_queue_empty
                            else -> R.string.dms_none }),
                            Modifier.padding(24.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    items(list, key = { "d" + it.id }) { d -> if (tiles) DocTile(d, entries, tresor, repo) { open = d } else DocRow(d, entries) { open = d } }
                    if (total > list.size) item(span = { GridItemSpan(maxLineSpan) }) { Text(stringResource(R.string.dms_more, total - list.size), Modifier.padding(16.dp),
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                }
            }
        }
        // Hochladen (klein) und Abfotografieren
        Column(Modifier.align(Alignment.BottomEnd).padding(16.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
            // Aus den Dateien von dmOffice (nicht im Tresor: dort liegen die Dokumente verschlüsselt)
            if (!tresor) SmallFloatingActionButton(onClick = { pickStored = true }) { Icon(Icons.Default.Cloud, contentDescription = stringResource(R.string.attach_from_files)) }
            SmallFloatingActionButton(onClick = { pick.launch(arrayOf("*/*")) }) { Icon(Icons.Default.UploadFile, contentDescription = stringResource(R.string.dms_upload)) }
            FloatingActionButton(onClick = scan.start) { Icon(Icons.Default.DocumentScanner, contentDescription = stringResource(R.string.dms_scan)) }
        }
    }

    acting?.let { e -> EntryActions(e, entries, repo, onDismiss = { acting = null }, onChanged = { entries = it; reload++ }) }
    pending?.let { files ->
        ProfileDialog(profiles.filter { (it.target == "tresor") == tresor }, onDismiss = { files.forEach { it.first.delete() }; pending = null }) { p ->
            pending = null; upload(files, p.id)
        }
    }
}

/** Ein Dokument in der Liste: Titel, Datum, WER und WAS, Tags; Posteingang fett, in Arbeit mit Hinweis. */
@Composable
private fun DocRow(d: DmsDoc, entries: List<DmsEntry>, onClick: () -> Unit) {
    val ctx = LocalContext.current
    val byId = remember(entries) { entries.associateBy { it.id } }
    val line = listOfNotNull(d.docDate?.let { dmsDate(it) }, d.correspondent?.let { byId[it]?.name }, d.doctype?.let { byId[it]?.name }).joinToString(" · ")
    ListItem(modifier = Modifier.clickable(onClick = onClick),
        headlineContent = { Text(d.title.ifBlank { d.originalName }, maxLines = 2, overflow = TextOverflow.Ellipsis,
            fontWeight = if (d.inbox) androidx.compose.ui.text.font.FontWeight.SemiBold else null) },
        supportingContent = {
            Column {
                when (d.state) {
                    "queued" -> Text(ctx.getString(R.string.dms_queued), color = MaterialTheme.colorScheme.primary)
                    "error" -> Text(d.error ?: ctx.getString(R.string.dms_error), color = MaterialTheme.colorScheme.error, maxLines = 2)
                    else -> if (line.isNotEmpty()) Text(line, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                if (d.tags.isNotEmpty()) Text(d.tags.mapNotNull { byId[it.id]?.name }.joinToString("  ") { "#$it" }, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
            }
        })
}

/** Kachel: Vorschau der ersten Seite, Titel, Datum und Korrespondent. */
@Composable
private fun DocTile(d: DmsDoc, entries: List<DmsEntry>, tresor: Boolean, repo: DmsRepo, onClick: () -> Unit) {
    val byId = remember(entries) { entries.associateBy { it.id } }
    val pdf = d.mime == "application/pdf" || d.path?.endsWith(".pdf", ignoreCase = true) == true
    var thumb by remember(d.id, d.path, tresor) { mutableStateOf<android.graphics.Bitmap?>(null) }
    LaunchedEffect(d.id, d.path, tresor, d.state) { if (pdf && d.state == "done") thumb = repo.thumb(d, tresor, 360) }
    Column(Modifier.padding(6.dp).clickable(onClick = onClick)) {
        Box(Modifier.fillMaxWidth().aspectRatio(1f / 1.414f).clip(RoundedCornerShape(6.dp)).background(MaterialTheme.colorScheme.surfaceVariant),
            contentAlignment = Alignment.Center) {
            val b = thumb
            when {
                b != null -> Image(b.asImageBitmap(), null, Modifier.fillMaxSize().background(Color.White), contentScale = ContentScale.Crop, alignment = Alignment.TopCenter)
                d.state == "queued" -> CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp)
                else -> Icon(Icons.Default.Description, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        Text(d.title.ifBlank { d.originalName }, Modifier.padding(top = 4.dp), style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis,
            fontWeight = if (d.inbox) androidx.compose.ui.text.font.FontWeight.SemiBold else null)
        Text(listOfNotNull(d.docDate?.let { dmsDate(it) }, d.correspondent?.let { byId[it]?.name }).joinToString(" · "),
            style = MaterialTheme.typography.bodySmall, color = if (d.state == "error") MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

/** Name eines Ordners im Baum: Einträge mit Oberbegriffen, Monate lesbar, ohne Angabe „Unbekannt“. */
internal fun nodeName(ctx: android.content.Context, n: DmsNode, entries: List<DmsEntry>): String = when {
    n.key == null -> ctx.getString(R.string.dms_unknown)
    n.level == "doc_month" || n.level == "import_month" -> try {
        java.time.YearMonth.parse(n.key).format(java.time.format.DateTimeFormatter.ofPattern("LLLL yyyy"))
    } catch (_: Exception) { n.name }
    n.level in setOf("correspondent", "doctype", "tag") -> entries.firstOrNull { it.id == n.key }?.name ?: n.name
    else -> n.name
}

/** Datum yyyy-MM-dd in der Schreibweise des Geräts. */
internal fun dmsDate(iso: String): String = try {
    java.time.LocalDate.parse(iso).format(java.time.format.DateTimeFormatter.ofLocalizedDate(java.time.format.FormatStyle.MEDIUM))
} catch (_: Exception) { iso }

/** Geteilte bzw. gewählte Datei in den Zwischenspeicher (die Leserechte gelten nur kurz): Datei, Name, Typ. */
internal fun copyToCache(ctx: android.content.Context, u: Uri): Triple<File, String, String?>? = try {
    var name: String? = null
    ctx.contentResolver.query(u, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c -> if (c.moveToFirst() && !c.isNull(0)) name = c.getString(0) }
    val n = name?.takeIf { it.isNotBlank() } ?: (u.lastPathSegment ?: "Dokument")
    val dir = File(ctx.cacheDir, "files/dms").apply { mkdirs() }
    val f = File(dir, System.nanoTime().toString() + "-" + n.replace('/', '_'))
    ctx.contentResolver.openInputStream(u)?.use { i -> f.outputStream().use { o -> i.copyTo(o) } } ?: throw java.io.IOException()
    Triple(f, n, ctx.contentResolver.getType(u))
} catch (_: Exception) { null }

/** Ablageprofil wählen (mehrere Profile). */
@Composable
internal fun ProfileDialog(profiles: List<DmsProfile>, onDismiss: () -> Unit, onPick: (DmsProfile) -> Unit) {
    androidx.compose.material3.AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.dms_pick_profile)) },
        text = {
            Column {
                profiles.sortedByDescending { it.default }.forEach { p ->
                    ListItem(modifier = Modifier.clickable { onPick(p) }, headlineContent = { Text(p.name) },
                        supportingContent = { Text(if (p.target == "tresor") stringResource(R.string.dms_tresor) else p.folder) },
                        leadingContent = { Icon(if (p.target == "tresor") Icons.Default.Lock else Icons.Default.Folder, contentDescription = null) })
                }
            }
        },
        confirmButton = {}, dismissButton = { androidx.compose.material3.TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}
