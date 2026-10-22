package at.dimes.dmoffice.ui

import android.Manifest
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material.icons.automirrored.filled.Sort
import androidx.compose.material.icons.automirrored.filled.ViewList
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.AudioFile
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.CloudUpload
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.CreateNewFolder
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.DeleteForever
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.DriveFileMove
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.FolderZip
import androidx.compose.material.icons.filled.GridView
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.OfflinePin
import androidx.compose.material.icons.filled.PhotoCamera
import androidx.compose.material.icons.filled.PictureAsPdf
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.RestoreFromTrash
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Slideshow
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material.icons.filled.TableChart
import androidx.compose.material.icons.filled.UploadFile
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.History
import androidx.compose.material.icons.outlined.Link
import androidx.compose.material.icons.outlined.PhotoLibrary
import androidx.compose.material.icons.outlined.StarOutline
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.core.content.FileProvider
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.files.ContentHit
import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.FileLink
import at.dimes.dmoffice.files.FileViews
import at.dimes.dmoffice.files.FilesProvider
import at.dimes.dmoffice.files.FilesRepo
import at.dimes.dmoffice.files.Listing
import at.dimes.dmoffice.files.Office
import at.dimes.dmoffice.files.OfficeConfig
import at.dimes.dmoffice.files.Thumbs
import at.dimes.dmoffice.files.TrashList
import at.dimes.dmoffice.files.parseTime
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.time.YearMonth
import java.time.format.TextStyle

/** Bereiche wie im Web (linke Leiste dort, hier eine Chip-Zeile). */
private enum class Area(val label: Int, val icon: ImageVector) {
    Files(R.string.files_area_files, Icons.Outlined.Folder),
    Recent(R.string.files_area_recent, Icons.Outlined.History),
    Favorites(R.string.files_area_favorites, Icons.Outlined.StarOutline),
    Photos(R.string.files_area_photos, Icons.Outlined.PhotoLibrary),
    Shared(R.string.files_area_shared, Icons.Outlined.Link),
    Trash(R.string.files_area_trash, Icons.Outlined.Delete),
}

/** Was gerade mit Eintraegen passiert (Dialoge). */
private sealed class FileAction {
    data object NewFolder : FileAction()
    data class Rename(val item: FileItem) : FileAction()
    data class Transfer(val items: List<FileItem>, val copy: Boolean) : FileAction()
    data class Delete(val items: List<FileItem>) : FileAction()
    data class Link(val item: FileItem, val existing: FileLink? = null) : FileAction()
    data class Versions(val item: FileItem) : FileAction()
    data class Details(val item: FileItem) : FileAction()
    data class DeleteLink(val link: FileLink) : FileAction()
    data class Purge(val ids: List<Long>) : FileAction()
    data object EmptyTrash : FileAction()
    data class NewOffice(val kind: String) : FileAction()
}

/** Ansicht der Dateiablage, auf dem Geraet gemerkt (wie die Einstellungen im Web). */
private class FilesUi(context: Context) {
    private val sp = context.getSharedPreferences("files_ui", Context.MODE_PRIVATE)
    var tiles by mutableStateOf(sp.getBoolean("tiles", false)); private set
    var tile by mutableStateOf(sp.getString("tile", "medium") ?: "medium"); private set
    var sort by mutableStateOf(sp.getString("sort", FileViews.SORT_NAME) ?: FileViews.SORT_NAME); private set
    var desc by mutableStateOf(sp.getBoolean("desc", false)); private set
    var group by mutableStateOf(sp.getString("group", FileViews.GROUP_NONE) ?: FileViews.GROUP_NONE); private set
    fun useTiles(v: Boolean) { tiles = v; sp.edit().putBoolean("tiles", v).apply() }
    fun useTile(v: String) { tile = v; sp.edit().putString("tile", v).apply() }
    fun useGroup(v: String) { group = v; sp.edit().putString("group", v).apply() }
    /** Wie im Web: nochmal dasselbe kehrt die Richtung um; Datum und Groesse beginnen absteigend. */
    fun sortBy(k: String) {
        if (k == sort) desc = !desc else { sort = k; desc = k == FileViews.SORT_MODIFIED || k == FileViews.SORT_SIZE }
        sp.edit().putString("sort", sort).putBoolean("desc", desc).apply()
    }
}

private val VIEWABLE = setOf(FileViews.IMAGE, FileViews.PDF, FileViews.TEXT, FileViews.AUDIO, FileViews.VIDEO)

/**
 * Dateiablage wie im Web: Bereiche (Meine Dateien, Zuletzt verwendet, Favoriten, Fotos, Freigegeben, Papierkorb),
 * Details- und Kachelansicht mit Vorschaubildern vom Server, Sortieren, Gruppieren, Suche nach Namen und Inhalt,
 * Mehrfachauswahl, Links, fruehere Versionen, Betrachter. Metadaten der Ordner liegen in Room (sofort da, ohne Netz).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FilesScreen(onClose: (() -> Unit)?, onAutoUpload: (() -> Unit)? = null) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).files
    val ui = remember { FilesUi(ctx) }
    val offState by repo.offline.state.collectAsState()
    val syncingRoots by repo.offline.syncing.collectAsState()
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }
    var area by rememberSaveable { mutableStateOf(Area.Files) }
    var path by rememberSaveable { mutableStateOf("") }
    var listing by remember { mutableStateOf<Listing?>(null) }
    var items by remember { mutableStateOf<List<FileItem>?>(null) }
    var used by remember { mutableStateOf<Map<String, Long>>(emptyMap()) }
    var links by remember { mutableStateOf<List<FileLink>?>(null) }
    var trash by remember { mutableStateOf<TrashList?>(null) }
    var loading by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var action by remember { mutableStateOf<FileAction?>(null) }
    var addMenu by remember { mutableStateOf(false) }
    var saveItem by remember { mutableStateOf<FileItem?>(null) }
    var cameraUri by rememberSaveable { mutableStateOf<String?>(null) }
    var tick by remember { mutableIntStateOf(0) }
    // Suche
    var searchOpen by rememberSaveable { mutableStateOf(false) }
    var query by rememberSaveable { mutableStateOf("") }
    var contentMode by rememberSaveable { mutableStateOf(false) }
    var kind by rememberSaveable { mutableStateOf<String?>(null) }
    var onlyHere by rememberSaveable { mutableStateOf(false) }
    var results by remember { mutableStateOf<List<FileItem>?>(null) }
    var hits by remember { mutableStateOf<at.dimes.dmoffice.files.ContentResult?>(null) }
    // Auswahl und Betrachter
    var selected by remember { mutableStateOf<Set<String>>(emptySet()) }
    var trashSel by remember { mutableStateOf<Set<Long>>(emptySet()) }
    var viewer by remember { mutableStateOf<Pair<List<FileItem>, Int>?>(null) }
    // Office-Server (Editor in OfficeActivity); null = noch nicht gefragt
    var office by remember { mutableStateOf<OfficeConfig?>(null) }
    val textUploading = stringResource(R.string.files_uploading)
    val textLoading = stringResource(R.string.files_loading)
    val textSyncing = stringResource(R.string.files_offline_syncing)
    val textUndo = stringResource(R.string.files_undo)

    val searching = searchOpen && (query.isNotBlank() || kind != null)

    /** Inhalt des aktuellen Bereichs laden; Ordner zuerst aus dem Speicher bzw. Room (sofort), dann frisch. */
    fun reload(clearError: Boolean = false) { tick++; if (clearError) error = null }

    LaunchedEffect(area, path, tick) {
        loading = true
        try {
            when (area) {
                Area.Files -> {
                    val p = path
                    if (listing?.path != p) listing = repo.cached(p)?.second ?: withContext(Dispatchers.IO) { repo.metaListing(p) }
                    val l = withContext(Dispatchers.IO) { repo.list(p) }
                    if (p == path) listing = l
                }
                Area.Recent -> {
                    val r = withContext(Dispatchers.IO) { repo.recent() }
                    used = r.associate { it.item.path to parseTime(it.used) }
                    items = r.map { it.item }
                }
                Area.Favorites -> items = withContext(Dispatchers.IO) { repo.favorites() }
                Area.Photos -> items = withContext(Dispatchers.IO) { repo.photos() }
                Area.Shared -> links = withContext(Dispatchers.IO) { repo.links() }
                Area.Trash -> trash = withContext(Dispatchers.IO) { repo.trash() }
            }
            error = null
        } catch (e: Exception) { error = e.message ?: e.toString() }
        finally { loading = false }
    }
    LaunchedEffect(tick) { office = withContext(Dispatchers.IO) { repo.officeConfig() } }
    // Suche: kurz nach dem Tippen, ueberall bzw. im aktuellen Ordner
    LaunchedEffect(query, contentMode, kind, searchOpen, onlyHere, tick) {
        if (!searching) { results = null; hits = null; return@LaunchedEffect }
        delay(350)
        // Wie im Web: ueberall, auf Wunsch nur im aktuellen Ordner
        val under = if (onlyHere && area == Area.Files) path else ""
        loading = true
        try {
            if (contentMode && query.isNotBlank()) { hits = withContext(Dispatchers.IO) { repo.searchContent(query.trim(), under) }; results = null }
            else { results = withContext(Dispatchers.IO) { repo.search(query.trim(), kind, under) }; hits = null }
            error = null
        } catch (e: Exception) { error = e.message ?: e.toString() } finally { loading = false }
    }

    /** Aenderung ausfuehren, danach neu laden und offene Ansichten in "Dateien" (Android) benachrichtigen. */
    fun run(label: String?, after: (suspend () -> Unit)? = null, block: suspend () -> Unit) {
        scope.launch {
            busy = label ?: ""
            error = null
            try { withContext(Dispatchers.IO) { block() }; after?.invoke() }
            catch (e: Exception) { android.util.Log.w("FilesScreen", "Aktion", e); error = e.message ?: e.toString() }
            finally { busy = null; FilesProvider.notifyChildren(ctx, path); selected = emptySet(); trashSel = emptySet(); reload() }
        }
    }

    // Offline-Ordner: nach jedem Abgleich die Liste aus der lokalen Kopie neu lesen
    val curRoot = offState.roots.keys.firstOrNull { at.dimes.dmoffice.files.Offline.inside(path, it) }
    val curRootSync = curRoot?.let { offState.roots[it]?.lastSync }
    LaunchedEffect(curRootSync) { if (curRootSync != null && curRootSync > 0 && area == Area.Files) reload() }

    // Zurueck: Auswahl, Suche, Ordner hoch, anderer Bereich -> Meine Dateien
    BackHandler(enabled = selected.isNotEmpty() || trashSel.isNotEmpty() || searchOpen || (area == Area.Files && path.isNotEmpty()) || area != Area.Files) {
        when {
            selected.isNotEmpty() || trashSel.isNotEmpty() -> { selected = emptySet(); trashSel = emptySet() }
            searchOpen -> { searchOpen = false; query = ""; kind = null }
            area != Area.Files -> area = Area.Files
            else -> path = FilesRepo.parent(path)
        }
    }

    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (uris.isNotEmpty()) { val dir = path; run(textUploading) { for (u in uris) repo.uploadUri(dir, u) } }
    }
    val camera = rememberLauncherForActivityResult(ActivityResultContracts.TakePicture()) { ok ->
        val u = cameraUri
        cameraUri = null
        if (ok && u != null) {
            val dir = path
            val name = "Foto " + java.time.LocalDateTime.now().format(java.time.format.DateTimeFormatter.ofPattern("yyyy-MM-dd HH.mm.ss")) + ".jpg"
            run(textUploading) {
                val f = File(ctx.cacheDir, "camera/" + Uri.parse(u).lastPathSegment)
                try { repo.upload(dir, FilesRepo.uniqueName(repo.listCached(dir, 5_000), name), "image/jpeg", f) } finally { f.delete() }
            }
        }
    }
    fun takePhoto() {
        val f = File(File(ctx.cacheDir, "camera").apply { mkdirs() }, "foto-${System.currentTimeMillis()}.jpg")
        val uri = FileProvider.getUriForFile(ctx, ctx.packageName + ".files", f)
        cameraUri = uri.toString()
        try { camera.launch(uri) } catch (e: Exception) { error = e.message }
    }
    val cameraPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted -> if (granted) takePhoto() }
    val saveTo = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        val item = saveItem
        saveItem = null
        if (uri != null && item != null) run(textLoading) {
            val f = repo.cacheFile(item)
            ctx.contentResolver.openOutputStream(uri)?.use { o -> f.inputStream().use { it.copyTo(o) } }
        }
    }

    // Office-Editor: danach neu laden, bei ONLYOFFICE speichert der Server erst einige Sekunden nach dem Schliessen
    val officeEditor = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        val dir = path
        reload(); FilesProvider.notifyChildren(ctx, dir)
        scope.launch { delay(15_000); reload(); FilesProvider.notifyChildren(ctx, dir) }
    }
    fun editOffice(item: FileItem, view: Boolean) {
        try { officeEditor.launch(OfficeActivity.intent(ctx, item.path, item.name, view)) } catch (e: Exception) { error = e.message ?: e.toString() }
    }
    val textExists = stringResource(R.string.files_office_exists)
    fun newOffice(kind: String, name: String) {
        val dir = path
        scope.launch {
            busy = ""; error = null
            try {
                val p = withContext(Dispatchers.IO) { repo.officeNew(dir, kind, name) }
                reload(); FilesProvider.notifyChildren(ctx, dir)
                editOffice(FileItem(p, p.substringAfterLast('/'), false), view = false)
            } catch (e: at.dimes.dmoffice.api.ApiException) { error = if (e.code == 409) textExists else e.message }
            catch (e: Exception) { error = e.message ?: e.toString() }
            finally { busy = null }
        }
    }

    /** In einer App oeffnen bzw. teilen (Datei vorher in den Cache). */
    fun external(list: List<FileItem>, share: Boolean) {
        scope.launch {
            busy = textLoading
            try {
                val files = withContext(Dispatchers.IO) { list.filter { !it.isDir }.map { it to repo.cacheFile(it) } }
                if (files.size == 1) openLocalFile(ctx, files[0].second, files[0].first, share) else if (files.isNotEmpty()) shareMany(ctx, files)
            } catch (e: Exception) { error = e.message ?: e.toString() }
            finally { busy = null }
        }
    }

    /** Antippen: Ordner oeffnen, Office-Dateien im Editor (wenn ein Office-Server da ist), ansehbare Dateien im Betrachter
     *  (Wischen durch die Liste), sonst in einer App. */
    fun open(item: FileItem, list: List<FileItem>) {
        if (item.isDir) { area = Area.Files; path = item.path; searchOpen = false; query = ""; kind = null; return }
        // Office-Server noch nicht gefragt (gleich nach dem Start): erst fragen, dann entscheiden
        if (office == null && FileViews.kindOf(item) in setOf(FileViews.DOC, FileViews.SHEET, FileViews.SLIDES)) {
            scope.launch { office = withContext(Dispatchers.IO) { repo.officeConfig() }; open(item, list) }
            return
        }
        if (Office.opensOnTap(item, office)) { editOffice(item, Office.mode(item, office) == Office.VIEW); return }
        if (FileViews.kindOf(item) in VIEWABLE) {
            val files = list.filter { !it.isDir && FileViews.kindOf(it) in VIEWABLE }
            viewer = files to files.indexOfFirst { it.path == item.path }.coerceAtLeast(0)
        } else external(listOf(item), share = false)
    }

    fun delete(list: List<FileItem>) {
        scope.launch {
            busy = ""
            try {
                val ids = withContext(Dispatchers.IO) { repo.deleteMany(list.map { it.path }) }
                selected = emptySet(); reload()
                if (ids.isNotEmpty() && snackbar.showSnackbar(ctx.resources.getQuantityString(R.plurals.files_deleted, list.size, list.size), textUndo, withDismissAction = true, duration = androidx.compose.material3.SnackbarDuration.Long) == SnackbarResult.ActionPerformed)
                    run(null) { repo.restore(ids) }
            } catch (e: Exception) { error = e.message ?: e.toString() } finally { busy = null; FilesProvider.notifyChildren(ctx, path) }
        }
    }

    fun favorite(list: List<FileItem>, on: Boolean) = run(null) { for (i in list) repo.setFavorite(i.path, on) }

    // Was gerade gezeigt wird
    val baseItems: List<FileItem> = when {
        searching -> results ?: emptyList()
        area == Area.Files -> listing?.items ?: emptyList()
        else -> items ?: emptyList()
    }
    val shown: List<FileItem> = when {
        !searching && area == Area.Recent -> baseItems
        !searching && area == Area.Photos -> baseItems
        else -> FileViews.sort(baseItems, ui.sort, ui.desc)
    }
    val groups = when {
        !searching && area == Area.Photos -> FileViews.byMonth(shown)
        !searching && area == Area.Recent -> listOf(FileViews.Group("", shown))
        else -> FileViews.group(shown, ui.group)
    }
    val showTiles = (ui.tiles || area == Area.Photos) && !(searching && contentMode)
    val selItems = shown.filter { it.path in selected }
    val showPath = searching || area == Area.Recent || area == Area.Favorites

    viewer?.let { (list, start) ->
        FileViewer(list, start, onClose = { viewer = null }, onShare = { external(listOf(it), share = true) }, onOpenExternal = { external(listOf(it), share = false) })
        return
    }

    // Als Reiter (onClose == null): ohne Zurueck-Pfeil, Systemleisten beruecksichtigt schon der Hauptbildschirm
    val insets = if (onClose == null) androidx.compose.foundation.layout.WindowInsets(0) else null
    Scaffold(
        contentWindowInsets = insets ?: androidx.compose.material3.ScaffoldDefaults.contentWindowInsets,
        snackbarHost = { SnackbarHost(snackbar) },
        topBar = {
            when {
                selected.isNotEmpty() -> SelectionBar(selItems, onClear = { selected = emptySet() },
                    onSelectAll = { selected = shown.map { it.path }.toSet() },
                    onAction = { a ->
                        val one = selItems.singleOrNull()
                        when (a) {
                            "link" -> one?.let { action = FileAction.Link(it) }
                            "fav" -> favorite(selItems, !selItems.all { it.favorite })
                            "move" -> action = FileAction.Transfer(selItems, copy = false)
                            "copy" -> action = FileAction.Transfer(selItems, copy = true)
                            "delete" -> action = FileAction.Delete(selItems)
                            "rename" -> one?.let { action = FileAction.Rename(it) }
                            "versions" -> one?.let { action = FileAction.Versions(it) }
                            "details" -> one?.let { action = FileAction.Details(it) }
                            "share" -> external(selItems, share = true)
                        }
                    }, insets = insets)
                trashSel.isNotEmpty() -> TopAppBar(windowInsets = insets ?: androidx.compose.material3.TopAppBarDefaults.windowInsets,
                    title = { Text(stringResource(R.string.files_selected, trashSel.size)) },
                    navigationIcon = { IconButton(onClick = { trashSel = emptySet() }) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } },
                    actions = {
                        IconButton(onClick = { val ids = trashSel.toList(); run(null) { repo.restore(ids) } }) { Icon(Icons.Default.RestoreFromTrash, contentDescription = stringResource(R.string.files_trash_restore)) }
                        IconButton(onClick = { action = FileAction.Purge(trashSel.toList()) }) { Icon(Icons.Default.DeleteForever, contentDescription = stringResource(R.string.files_trash_purge)) }
                    })
                searchOpen -> TopAppBar(windowInsets = insets ?: androidx.compose.material3.TopAppBarDefaults.windowInsets,
                    navigationIcon = { IconButton(onClick = { searchOpen = false; query = ""; kind = null }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
                    title = {
                        val focus = remember { androidx.compose.ui.focus.FocusRequester() }
                        LaunchedEffect(Unit) { try { focus.requestFocus() } catch (_: Exception) { } }
                        TextField(value = query, onValueChange = { query = it }, singleLine = true, modifier = Modifier.fillMaxWidth().focusRequester(focus),
                            placeholder = { Text(stringResource(if (contentMode) R.string.files_search_content_hint else R.string.files_search_hint)) },
                            colors = TextFieldDefaults.colors(focusedContainerColor = Color.Transparent, unfocusedContainerColor = Color.Transparent))
                    },
                    actions = { if (query.isNotEmpty()) IconButton(onClick = { query = "" }) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } })
                else -> TopAppBar(title = { Text(stringResource(if (area == Area.Files) R.string.files else area.label)) },
                    windowInsets = insets ?: androidx.compose.material3.TopAppBarDefaults.windowInsets,
                    navigationIcon = { if (onClose != null) IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
                    actions = {
                        if (area != Area.Shared && area != Area.Trash) IconButton(onClick = { searchOpen = true }) { Icon(Icons.Default.Search, contentDescription = stringResource(R.string.files_search)) }
                        if (area != Area.Shared && area != Area.Trash && area != Area.Photos)
                            IconButton(onClick = { ui.useTiles(!ui.tiles) }) { Icon(if (ui.tiles) Icons.AutoMirrored.Filled.ViewList else Icons.Default.GridView, contentDescription = stringResource(if (ui.tiles) R.string.files_view_details else R.string.files_view_tiles)) }
                        if (area != Area.Shared && area != Area.Trash) ViewMenu(ui, tilesActive = ui.tiles || area == Area.Photos, sortable = area != Area.Recent && area != Area.Photos)
                        MoreMenu(area, onNewFolder = { action = FileAction.NewFolder }, onAutoUpload = onAutoUpload, onRefresh = { reload(clearError = true) },
                            onEmptyTrash = { action = FileAction.EmptyTrash })
                    })
            }
        },
        // Belegung unten als eigene Leiste, damit der Knopf "Hochladen" sie nicht verdeckt
        bottomBar = { if (area == Area.Files && !searching) listing?.let { Box(Modifier.navigationBarsPadding()) { UsageBar(if (it.quota > 0 && it.used > 0) it.used else it.usage, it.quota) } } },
        floatingActionButton = {
            if (area == Area.Files && !searching && selected.isEmpty()) Box {
                ExtendedFloatingActionButton(onClick = { addMenu = true }, icon = { Icon(Icons.Default.Add, contentDescription = null) }, text = { Text(stringResource(R.string.files_upload)) })
                DropdownMenu(expanded = addMenu, onDismissRequest = { addMenu = false }) {
                    DropdownMenuItem(text = { Text(stringResource(R.string.files_upload_file)) }, leadingIcon = { Icon(Icons.Default.UploadFile, contentDescription = null) },
                        onClick = { addMenu = false; pick.launch(arrayOf("*/*")) })
                    DropdownMenuItem(text = { Text(stringResource(R.string.files_take_photo)) }, leadingIcon = { Icon(Icons.Default.PhotoCamera, contentDescription = null) },
                        onClick = {
                            addMenu = false
                            if (ContextCompat.checkSelfPermission(ctx, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) takePhoto()
                            else cameraPermission.launch(Manifest.permission.CAMERA)
                        })
                    DropdownMenuItem(text = { Text(stringResource(R.string.files_new_folder)) }, leadingIcon = { Icon(Icons.Default.CreateNewFolder, contentDescription = null) },
                        onClick = { addMenu = false; action = FileAction.NewFolder })
                    // Neue Office-Dokumente, wenn ein Office-Server sie bearbeiten kann
                    for (k in office?.takeIf { it.enabled }?.create ?: emptyList()) {
                        val (label, icon) = when (k) {
                            "docx" -> R.string.files_office_docx to Icons.Default.Description
                            "xlsx" -> R.string.files_office_xlsx to Icons.Default.TableChart
                            "pptx" -> R.string.files_office_pptx to Icons.Default.Slideshow
                            else -> continue
                        }
                        DropdownMenuItem(text = { Text(stringResource(label)) }, leadingIcon = { Icon(icon, contentDescription = null) },
                            onClick = { addMenu = false; action = FileAction.NewOffice(k) })
                    }
                }
            }
        }
    ) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            if (searchOpen) SearchFilters(contentMode, kind, if (area == Area.Files && path.isNotEmpty()) onlyHere else null, onContent = { contentMode = it; if (it) kind = null }, onKind = { kind = it }, onHere = { onlyHere = it })
            else if (selected.isEmpty() && trashSel.isEmpty()) AreaChips(area) { a -> area = a; selected = emptySet(); trashSel = emptySet() }
            if (area == Area.Files && !searching) {
                Breadcrumb(path) { path = it }
                if (listing?.cached == true && curRoot == null) Text(stringResource(R.string.files_cached_state), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.tertiary, modifier = Modifier.padding(horizontal = 16.dp, vertical = 2.dp))
                if (curRoot != null) OfflineBanner(offState.roots[curRoot], curRoot in syncingRoots) { val p = path; run(textSyncing) { repo.offline.sync(p) } }
            }
            if (busy != null || loading) LinearProgressIndicator(Modifier.fillMaxWidth())
            busy?.takeIf { it.isNotEmpty() }?.let { Text(it, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp)) }
            error?.let { Text(stringResource(R.string.error_generic, it), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) }
            Box(Modifier.weight(1f).fillMaxWidth()) {
                when {
                    searching && contentMode -> ContentHits(hits, onOpen = { h ->
                        val item = FileItem(h.path, h.name, false, h.size, h.mime, h.modified)
                        open(item, listOf(item))
                    })
                    !searching && area == Area.Shared -> {
                        val l = links
                        if (l != null && l.isEmpty()) Empty(stringResource(R.string.files_shared_empty))
                        LazyColumn(Modifier.fillMaxSize()) {
                            items(l ?: emptyList(), key = { it.id }) { link ->
                                LinkRow(link, onEdit = { link.item?.let { action = FileAction.Link(it, link) } }, onDelete = { action = FileAction.DeleteLink(link) })
                                HorizontalDivider()
                            }
                        }
                    }
                    !searching && area == Area.Trash -> TrashView(trash, trashSel, onToggle = { id -> trashSel = if (id in trashSel) trashSel - id else trashSel + id },
                        onRestore = { id -> run(null) { repo.restore(listOf(id)) } })
                    else -> {
                        val emptyText = when {
                            searching -> stringResource(R.string.files_search_none)
                            area == Area.Favorites -> stringResource(R.string.files_favorites_empty)
                            area == Area.Photos -> stringResource(R.string.files_photos_empty)
                            area == Area.Recent -> stringResource(R.string.files_recent_empty)
                            else -> stringResource(R.string.files_empty)
                        }
                        val loaded = if (searching) results != null else if (area == Area.Files) listing != null else items != null
                        if (loaded && shown.isEmpty()) Empty(emptyText)
                        else if (!loaded && loading) CircularProgressIndicator(Modifier.align(Alignment.Center))
                        val onClick: (FileItem) -> Unit = { item ->
                            if (selected.isNotEmpty()) selected = if (item.path in selected) selected - item.path else selected + item.path
                            else open(item, groups.flatMap { it.items })
                        }
                        val onLong: (FileItem) -> Unit = { item -> selected = if (item.path in selected) selected - item.path else selected + item.path }
                        val onMenu: (FileItem, String) -> Unit = { item, a ->
                            when (a) {
                                "open" -> open(item, groups.flatMap { it.items })
                                "openwith" -> external(listOf(item), share = false)
                                "office" -> editOffice(item, Office.mode(item, office) == Office.VIEW)
                                "share" -> external(listOf(item), share = true)
                                "save" -> { saveItem = item; saveTo.launch(item.name) }
                                "link" -> action = FileAction.Link(item)
                                "fav" -> favorite(listOf(item), !item.favorite)
                                "rename" -> action = FileAction.Rename(item)
                                "move" -> action = FileAction.Transfer(listOf(item), copy = false)
                                "copy" -> action = FileAction.Transfer(listOf(item), copy = true)
                                "versions" -> action = FileAction.Versions(item)
                                "details" -> action = FileAction.Details(item)
                                "select" -> selected = selected + item.path
                                "offline" -> if (offState.roots.containsKey(item.path)) run(null) { repo.offline.unmark(item.path) }
                                    else run(textSyncing) { repo.offline.mark(item.path); repo.offline.sync(item.path) }
                                "delete" -> action = FileAction.Delete(listOf(item))
                            }
                        }
                        val offlineInfo: (FileItem) -> Pair<Boolean, Boolean> = { item ->
                            val isRoot = item.isDir && offState.roots.containsKey(item.path)
                            isRoot to (item.isDir && area == Area.Files && !searching && curRoot == null && !isRoot)
                        }
                        // Neue Liste (Ordner, Bereich, Sortierung, Gruppen): oben anfangen, sonst bleibt eine Gruppenueberschrift weggescrollt
                        androidx.compose.runtime.key(area, path, ui.sort, ui.desc, ui.group, searching) {
                        if (showTiles) TileGrid(groups, ui.tile, selected, onClick, onLong, groupLabel = { groupLabel(ctx, it, area == Area.Photos && !searching, ui.group) })
                        else DetailList(groups, selected, showPath, if (area == Area.Recent && !searching) used else null, onClick, onLong, onMenu, offlineInfo,
                            officeOf = { Office.mode(it, office) },
                            groupLabel = { groupLabel(ctx, it, false, ui.group) })
                        }
                    }
                }
            }
        }
    }

    when (val a = action) {
        FileAction.NewFolder -> NameDialog(stringResource(R.string.files_new_folder), "", onDismiss = { action = null }) { name ->
            action = null
            val dir = path
            run(null) { repo.mkdir(FilesRepo.join(dir, name)) }
        }
        is FileAction.Rename -> NameDialog(stringResource(R.string.note_rename), a.item.name, onDismiss = { action = null }) { name ->
            action = null
            if (name != a.item.name) run(null) { repo.transfer(a.item.path, FilesRepo.join(FilesRepo.parent(a.item.path), name), copy = false) }
        }
        is FileAction.Transfer -> FolderPickerDialog(start = FilesRepo.parent(a.items.first().path), exclude = a.items.singleOrNull()?.takeIf { it.isDir && !a.copy }?.path,
            onDismiss = { action = null }, title = stringResource(if (a.copy) R.string.files_copy else R.string.files_move),
            confirm = stringResource(if (a.copy) R.string.files_copy_here else R.string.files_move_here)) { dir ->
            action = null
            run(null) {
                // Kopie in denselben Ordner oder Namensgleichheit: Nummer anhaengen statt Fehler
                for (i in a.items) if (a.copy || dir != FilesRepo.parent(i.path)) repo.transfer(i.path, FilesRepo.join(dir, i.name), a.copy, if (a.copy) "rename" else "fail")
                FilesProvider.notifyChildren(ctx, dir)
            }
        }
        is FileAction.Delete -> ConfirmDialog(stringResource(R.string.delete),
            a.items.singleOrNull()?.let { stringResource(if (it.isDir) R.string.files_delete_folder_confirm else R.string.files_delete_confirm, it.name) }
                ?: stringResource(R.string.files_delete_many_confirm, a.items.size),
            stringResource(R.string.delete), onDismiss = { action = null }) { action = null; delete(a.items) }
        is FileAction.Link -> LinkDialog(a.item, a.existing, onDismiss = { action = null }, onDone = { action = null; selected = emptySet(); reload() })
        is FileAction.Versions -> VersionsDialog(a.item, onDismiss = { action = null }, onChanged = { reload() })
        is FileAction.Details -> DetailsDialog(a.item, onDismiss = { action = null })
        is FileAction.DeleteLink -> ConfirmDialog(stringResource(R.string.files_link_delete), stringResource(R.string.files_link_delete_confirm), stringResource(R.string.delete),
            onDismiss = { action = null }) { action = null; run(null) { repo.deleteLink(a.link.id) } }
        is FileAction.Purge -> ConfirmDialog(stringResource(R.string.files_trash_purge), androidx.compose.ui.res.pluralStringResource(R.plurals.files_trash_purge_confirm, a.ids.size, a.ids.size), stringResource(R.string.files_trash_purge),
            onDismiss = { action = null }) { action = null; run(null) { repo.purge(a.ids) } }
        is FileAction.NewOffice -> {
            val title = stringResource(when (a.kind) { "xlsx" -> R.string.files_office_new_xlsx; "pptx" -> R.string.files_office_new_pptx; else -> R.string.files_office_new_docx })
            val base = Office.newName(stringResource(R.string.files_office_new_name), a.kind, listing?.items?.map { it.name } ?: emptyList())
            NameDialog(title, "$base.${a.kind}", onDismiss = { action = null }) { name -> action = null; newOffice(a.kind, name) }
        }
        FileAction.EmptyTrash -> ConfirmDialog(stringResource(R.string.files_trash_empty), stringResource(R.string.files_trash_empty_confirm), stringResource(R.string.files_trash_empty),
            onDismiss = { action = null }) { action = null; run(null) { repo.emptyTrash() } }
        null -> {}
    }
}

@Composable
private fun Empty(text: String) = Text(text, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(16.dp))

/** Beschriftung einer Gruppe: Art, Datumsgruppe oder Monat (Galerie). */
private fun groupLabel(ctx: Context, key: String, month: Boolean, by: String): String {
    if (key.isEmpty()) return ""
    if (month) return try { val ym = YearMonth.parse(key); ym.month.getDisplayName(TextStyle.FULL_STANDALONE, java.util.Locale.getDefault()) + " " + ym.year } catch (_: Exception) { key }
    if (by == FileViews.GROUP_DATE) return ctx.getString(when (key) { "0" -> R.string.files_group_today; "1" -> R.string.files_group_yesterday; "2" -> R.string.files_group_week
        "3" -> R.string.files_group_last_week; "4" -> R.string.files_group_month; "5" -> R.string.files_group_year; else -> R.string.files_group_older })
    return kindLabel(ctx, key)
}

internal fun kindLabel(ctx: Context, kind: String): String = ctx.getString(when (kind) {
    FileViews.FOLDER -> R.string.files_kind_folder; FileViews.IMAGE -> R.string.files_kind_image; FileViews.PDF -> R.string.files_kind_pdf
    FileViews.DOC -> R.string.files_kind_doc; FileViews.SHEET -> R.string.files_kind_sheet; FileViews.SLIDES -> R.string.files_kind_slides
    FileViews.AUDIO -> R.string.files_kind_audio; FileViews.VIDEO -> R.string.files_kind_video; FileViews.ARCHIVE -> R.string.files_kind_archive
    FileViews.TEXT -> R.string.files_kind_text; else -> R.string.files_kind_other
})

@Composable
private fun AreaChips(area: Area, onArea: (Area) -> Unit) {
    // Gewaehlten Bereich sichtbar halten (die Leiste ist breiter als das Telefon)
    val state = androidx.compose.foundation.lazy.rememberLazyListState(initialFirstVisibleItemIndex = (area.ordinal - 1).coerceAtLeast(0))
    LaunchedEffect(area) { state.animateScrollToItem((area.ordinal - 1).coerceAtLeast(0)) }
    LazyRow(state = state, contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        items(Area.entries.toList()) { a ->
            FilterChip(selected = a == area, onClick = { onArea(a) }, label = { Text(stringResource(a.label)) },
                leadingIcon = { Icon(a.icon, contentDescription = null, modifier = Modifier.size(18.dp)) })
        }
    }
}

/** Unter dem Suchfeld: Namen oder Inhalt, Filter nach Art. */
@Composable
private fun SearchFilters(content: Boolean, kind: String?, here: Boolean?, onContent: (Boolean) -> Unit, onKind: (String?) -> Unit, onHere: (Boolean) -> Unit) {
    val ctx = LocalContext.current
    LazyRow(contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        item { FilterChip(selected = !content, onClick = { onContent(false) }, label = { Text(stringResource(R.string.files_search_names)) }) }
        item { FilterChip(selected = content, onClick = { onContent(true) }, label = { Text(stringResource(R.string.files_search_content)) }) }
        if (here != null) item { FilterChip(selected = here, onClick = { onHere(!here) }, label = { Text(stringResource(R.string.files_search_here)) }) }
        if (!content) items(FileViews.FILTER_KINDS) { k ->
            FilterChip(selected = kind == k, onClick = { onKind(if (kind == k) null else k) }, label = { Text(kindLabel(ctx, k)) })
        }
    }
}

/** Sortieren, Gruppieren, Kachelgroesse. */
@Composable
private fun ViewMenu(ui: FilesUi, tilesActive: Boolean, sortable: Boolean) {
    val ctx = LocalContext.current
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) { Icon(Icons.AutoMirrored.Filled.Sort, contentDescription = stringResource(R.string.files_view)) }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            fun check(on: Boolean) = if (on) "✓  " else "     "
            if (sortable) {
                Text(stringResource(R.string.files_sort), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
                for ((k, l) in listOf(FileViews.SORT_NAME to R.string.files_sort_name, FileViews.SORT_MODIFIED to R.string.files_sort_date, FileViews.SORT_SIZE to R.string.files_sort_size, FileViews.SORT_KIND to R.string.files_sort_kind))
                    DropdownMenuItem(text = { Text(check(ui.sort == k) + ctx.getString(l) + if (ui.sort == k) (if (ui.desc) "  ↓" else "  ↑") else "") }, onClick = { ui.sortBy(k) })
                HorizontalDivider()
                Text(stringResource(R.string.files_group), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
                for ((k, l) in listOf(FileViews.GROUP_NONE to R.string.files_group_none, FileViews.GROUP_KIND to R.string.files_group_kind, FileViews.GROUP_DATE to R.string.files_group_date))
                    DropdownMenuItem(text = { Text(check(ui.group == k) + ctx.getString(l)) }, onClick = { ui.useGroup(k) })
            }
            if (tilesActive) {
                HorizontalDivider()
                Text(stringResource(R.string.files_tile_size), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
                for ((k, l) in listOf("small" to R.string.files_tile_small, "medium" to R.string.files_tile_medium, "large" to R.string.files_tile_large))
                    DropdownMenuItem(text = { Text(check(ui.tile == k) + ctx.getString(l)) }, onClick = { ui.useTile(k) })
            }
        }
    }
}

@Composable
private fun MoreMenu(area: Area, onNewFolder: () -> Unit, onAutoUpload: (() -> Unit)?, onRefresh: () -> Unit, onEmptyTrash: () -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            if (area == Area.Files) DropdownMenuItem(text = { Text(stringResource(R.string.files_new_folder)) }, leadingIcon = { Icon(Icons.Default.CreateNewFolder, null) }, onClick = { open = false; onNewFolder() })
            if (area == Area.Trash) DropdownMenuItem(text = { Text(stringResource(R.string.files_trash_empty)) }, leadingIcon = { Icon(Icons.Default.DeleteForever, null) }, onClick = { open = false; onEmptyTrash() })
            DropdownMenuItem(text = { Text(stringResource(R.string.refresh)) }, leadingIcon = { Icon(Icons.Default.Refresh, null) }, onClick = { open = false; onRefresh() })
            if (onAutoUpload != null) DropdownMenuItem(text = { Text(stringResource(R.string.au_title)) }, leadingIcon = { Icon(Icons.Default.CloudUpload, null) }, onClick = { open = false; onAutoUpload() })
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SelectionBar(sel: List<FileItem>, onClear: () -> Unit, onSelectAll: () -> Unit, onAction: (String) -> Unit, insets: androidx.compose.foundation.layout.WindowInsets?) {
    var more by remember { mutableStateOf(false) }
    val one = sel.singleOrNull()
    TopAppBar(windowInsets = insets ?: androidx.compose.material3.TopAppBarDefaults.windowInsets,
        title = { Text(stringResource(R.string.files_selected, sel.size), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleMedium) },
        navigationIcon = { IconButton(onClick = onClear) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } },
        actions = {
            if (one != null) IconButton(onClick = { onAction("link") }) { Icon(Icons.Default.Link, contentDescription = stringResource(R.string.files_link_new)) }
            IconButton(onClick = { onAction("fav") }) { Icon(if (sel.isNotEmpty() && sel.all { it.favorite }) Icons.Default.Star else Icons.Default.StarBorder, contentDescription = stringResource(R.string.files_favorite)) }
            IconButton(onClick = { onAction("move") }) { Icon(Icons.Default.DriveFileMove, contentDescription = stringResource(R.string.files_move)) }
            IconButton(onClick = { onAction("delete") }) { Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.delete)) }
            Box {
                IconButton(onClick = { more = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
                DropdownMenu(expanded = more, onDismissRequest = { more = false }) {
                    DropdownMenuItem(text = { Text(stringResource(R.string.files_copy)) }, leadingIcon = { Icon(Icons.Default.ContentCopy, null) }, onClick = { more = false; onAction("copy") })
                    if (sel.any { !it.isDir }) DropdownMenuItem(text = { Text(stringResource(R.string.files_share)) }, onClick = { more = false; onAction("share") })
                    if (one != null) {
                        DropdownMenuItem(text = { Text(stringResource(R.string.note_rename)) }, onClick = { more = false; onAction("rename") })
                        if (!one.isDir) DropdownMenuItem(text = { Text(stringResource(R.string.files_versions)) }, onClick = { more = false; onAction("versions") })
                        DropdownMenuItem(text = { Text(stringResource(R.string.files_details)) }, onClick = { more = false; onAction("details") })
                    }
                    DropdownMenuItem(text = { Text(stringResource(R.string.files_select_all)) }, onClick = { more = false; onSelectAll() })
                }
            }
        })
}

/** Vorschaubild vom Server (zwischengespeichert), sonst das Symbol der Art. */
@Composable
internal fun ThumbOrIcon(item: FileItem, modifier: Modifier, iconSize: androidx.compose.ui.unit.Dp, offlineRoot: Boolean = false) {
    val ctx = LocalContext.current
    val bmp by produceState(if (FileViews.hasThumb(item)) Thumbs.cached(item, Thumbs.SMALL) else null, item.path, item.etag) {
        if (value == null && FileViews.hasThumb(item)) value = try { Thumbs.load(ctx, item, Thumbs.SMALL) } catch (_: Exception) { null }
    }
    Box(modifier, contentAlignment = Alignment.Center) {
        val b = bmp
        if (b != null) Image(b.asImageBitmap(), contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        else Icon(if (offlineRoot) Icons.Default.OfflinePin else iconFor(item), contentDescription = null, modifier = Modifier.size(iconSize),
            tint = if (item.isDir) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun DetailList(groups: List<FileViews.Group>, selected: Set<String>, showPath: Boolean, used: Map<String, Long>?,
                       onClick: (FileItem) -> Unit, onLong: (FileItem) -> Unit, onMenu: (FileItem, String) -> Unit,
                       offlineInfo: (FileItem) -> Pair<Boolean, Boolean>, officeOf: (FileItem) -> String?, groupLabel: (String) -> String) {
    val ctx = LocalContext.current
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
        for (g in groups) {
            if (g.key.isNotEmpty()) item(key = "g:" + g.key) { SectionHeader(groupLabel(g.key)) }
            items(g.items, key = { it.path }) { item ->
                val (isRoot, canOffline) = offlineInfo(item)
                val sel = item.path in selected
                val sub = buildList {
                    if (showPath) add("/" + FilesRepo.parent(item.path))
                    if (!item.isDir) add(sizeText(item.size))
                    val t = used?.get(item.path)?.takeIf { it > 0 } ?: item.modifiedMs
                    if (t > 0) add(Fmt.dateTime(ctx, t, false))
                    if (isRoot) add(ctx.getString(R.string.files_offline))
                }.joinToString(" · ")
                ListItem(
                    modifier = Modifier.combinedClickable(onClick = { onClick(item) }, onLongClick = { onLong(item) })
                        .then(if (sel) Modifier.background(MaterialTheme.colorScheme.secondaryContainer) else Modifier),
                    leadingContent = { ThumbOrIcon(item, Modifier.size(44.dp).clip(RoundedCornerShape(6.dp)), 28.dp, isRoot) },
                    headlineContent = {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(item.name, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                            if (item.favorite) Icon(Icons.Default.Star, contentDescription = null, tint = Color(0xFFF2B600), modifier = Modifier.padding(start = 4.dp).size(16.dp))
                            if (item.shared) Icon(Icons.Default.Link, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(start = 4.dp).size(16.dp))
                        }
                    },
                    supportingContent = if (sub.isNotEmpty()) ({ Text(sub, maxLines = 1, overflow = TextOverflow.Ellipsis) }) else null,
                    trailingContent = {
                        if (selected.isNotEmpty()) Checkbox(checked = sel, onCheckedChange = { onClick(item) })
                        else ItemMenu(item, isRoot, canOffline, officeOf(item)) { onMenu(item, it) }
                    })
            }
        }
    }
}

@Composable
private fun ItemMenu(item: FileItem, offlineRoot: Boolean, canMarkOffline: Boolean, officeMode: String?, onAction: (String) -> Unit) {
    var menu by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
            @Composable fun entry(label: Int, key: String, color: Color = Color.Unspecified) =
                DropdownMenuItem(text = { Text(stringResource(label), color = color) }, onClick = { menu = false; onAction(key) })
            if (!item.isDir) {
                entry(R.string.files_open, "open")
                if (officeMode != null) entry(if (officeMode == Office.EDIT) R.string.files_office_edit else R.string.files_office_view, "office")
                entry(R.string.files_open_with, "openwith")
                entry(R.string.files_share, "share")
                entry(R.string.files_save_to_device, "save")
            }
            entry(R.string.files_link_new, "link")
            entry(if (item.favorite) R.string.files_favorite_remove else R.string.files_favorite_add, "fav")
            if (offlineRoot) entry(R.string.files_offline_remove, "offline") else if (canMarkOffline) entry(R.string.files_offline_make, "offline")
            entry(R.string.note_rename, "rename")
            entry(R.string.files_move, "move")
            entry(R.string.files_copy, "copy")
            if (!item.isDir) entry(R.string.files_versions, "versions")
            entry(R.string.files_details, "details")
            entry(R.string.files_select, "select")
            entry(R.string.delete, "delete", MaterialTheme.colorScheme.error)
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun TileGrid(groups: List<FileViews.Group>, tile: String, selected: Set<String>, onClick: (FileItem) -> Unit, onLong: (FileItem) -> Unit, groupLabel: (String) -> String) {
    val min = when (tile) { "small" -> 88.dp; "large" -> 180.dp; else -> 120.dp }
    LazyVerticalGrid(GridCells.Adaptive(min), Modifier.fillMaxSize(), contentPadding = PaddingValues(start = 8.dp, end = 8.dp, bottom = 88.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        for (g in groups) {
            if (g.key.isNotEmpty()) item(key = "g:" + g.key, span = { GridItemSpan(maxLineSpan) }) { SectionHeader(groupLabel(g.key)) }
            items(g.items.size, key = { g.items[it].path }) { n ->
                val item = g.items[n]
                val sel = item.path in selected
                Column(Modifier.clip(RoundedCornerShape(8.dp)).combinedClickable(onClick = { onClick(item) }, onLongClick = { onLong(item) })
                    .then(if (sel) Modifier.border(3.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(8.dp)) else Modifier)) {
                    Box {
                        ThumbOrIcon(item, Modifier.fillMaxWidth().aspectRatio(1f).background(MaterialTheme.colorScheme.surfaceVariant), if (tile == "small") 32.dp else 48.dp)
                        if (sel) Icon(Icons.Default.CheckCircle, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.align(Alignment.TopEnd).padding(4.dp))
                        if (item.favorite) Icon(Icons.Default.Star, contentDescription = null, tint = Color(0xFFF2B600), modifier = Modifier.align(Alignment.TopStart).padding(4.dp).size(16.dp))
                    }
                    if (tile != "small" || !FileViews.hasThumb(item))
                        Text(item.name, style = MaterialTheme.typography.bodySmall, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(4.dp))
                }
            }
        }
    }
}

/** Treffer der Inhaltssuche mit Ausschnitt; die gefundenen Woerter fett. */
@Composable
private fun ContentHits(r: at.dimes.dmoffice.files.ContentResult?, onOpen: (ContentHit) -> Unit) {
    val ctx = LocalContext.current
    if (r == null) return
    if (!r.enabled) { Empty(stringResource(R.string.files_search_content_off)); return }
    if (r.items.isEmpty()) { Empty(stringResource(R.string.files_search_none)); return }
    LazyColumn(Modifier.fillMaxSize()) {
        items(r.items, key = { it.path }) { h ->
            val (text, marks) = FileViews.snippet(h.snippet)
            val styled = buildAnnotatedString {
                append(text)
                for (m in marks) addStyle(SpanStyle(fontWeight = FontWeight.Bold, background = MaterialTheme.colorScheme.tertiaryContainer), m.first, m.last + 1)
            }
            ListItem(modifier = Modifier.clickable { onOpen(h) },
                leadingContent = { Icon(iconFor(FileItem(h.path, h.name, false, h.size, h.mime)), contentDescription = null) },
                headlineContent = { Text(h.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                supportingContent = { Column {
                    Text("/" + FilesRepo.parent(h.path) + " · " + Fmt.dateTime(ctx, parseTime(h.modified), false), style = MaterialTheme.typography.bodySmall, maxLines = 1)
                    Text(styled, style = MaterialTheme.typography.bodySmall, maxLines = 3, overflow = TextOverflow.Ellipsis)
                } })
            HorizontalDivider()
        }
    }
}


/** Papierkorb: wiederherstellen (einzeln oder Auswahl), endgueltig loeschen, leeren. */
@Composable
private fun TrashView(t: TrashList?, sel: Set<Long>, onToggle: (Long) -> Unit, onRestore: (Long) -> Unit) {
    val ctx = LocalContext.current
    if (t == null) return
    Column {
        Text(if (t.days > 0) stringResource(R.string.files_trash_days, t.days) else stringResource(R.string.files_trash_keep), style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
        if (t.items.isEmpty()) Empty(stringResource(R.string.files_trash_none))
        LazyColumn(Modifier.fillMaxSize()) {
            items(t.items, key = { it.id }) { e ->
                ListItem(modifier = Modifier.clickable { onToggle(e.id) },
                    leadingContent = { Checkbox(checked = e.id in sel, onCheckedChange = { onToggle(e.id) }) },
                    headlineContent = { Text(e.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    supportingContent = { Text("/" + FilesRepo.parent(e.path) + (if (!e.isDir) " · " + sizeText(e.size) else "") + " · " +
                        stringResource(R.string.files_trash_deleted, Fmt.dateTime(ctx, parseTime(e.deleted), false)), maxLines = 2) },
                    trailingContent = { IconButton(onClick = { onRestore(e.id) }) { Icon(Icons.Default.RestoreFromTrash, contentDescription = stringResource(R.string.files_trash_restore)) } })
                HorizontalDivider()
            }
        }
    }
}

/** Pfad als Kette: Dateien › Ordner › Unterordner; jedes Glied springt dorthin. */
@Composable
private fun Breadcrumb(path: String, onGo: (String) -> Unit) {
    val parts = if (path.isEmpty()) emptyList() else path.split('/')
    val scroll = rememberScrollState()
    LaunchedEffect(path) { scroll.animateScrollTo(scroll.maxValue) }
    Row(Modifier.fillMaxWidth().horizontalScroll(scroll).padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        if (path.isNotEmpty()) IconButton(onClick = { onGo(FilesRepo.parent(path)) }) { Icon(Icons.Default.ArrowUpward, contentDescription = stringResource(R.string.files_up)) }
        TextButton(onClick = { onGo("") }) { Text(stringResource(R.string.files_area_files)) }
        parts.forEachIndexed { i, name ->
            Icon(Icons.Default.ChevronRight, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
            val target = parts.take(i + 1).joinToString("/")
            TextButton(onClick = { onGo(target) }, enabled = target != path) { Text(name, maxLines = 1) }
        }
    }
}

private fun iconFor(item: FileItem) = when (FileViews.kindOf(item)) {
    FileViews.FOLDER -> Icons.Default.Folder
    FileViews.IMAGE -> Icons.Default.Image
    FileViews.VIDEO -> Icons.Default.Movie
    FileViews.AUDIO -> Icons.Default.AudioFile
    FileViews.PDF -> Icons.Default.PictureAsPdf
    FileViews.ARCHIVE -> Icons.Default.FolderZip
    FileViews.SHEET -> Icons.Default.TableChart
    FileViews.DOC, FileViews.SLIDES, FileViews.TEXT -> Icons.Default.Description
    else -> Icons.AutoMirrored.Filled.InsertDriveFile
}

/** In einem Offline-Ordner: Stand des Abgleichs und "Jetzt abgleichen". */
@Composable
private fun OfflineBanner(root: at.dimes.dmoffice.files.Offline.Root?, syncing: Boolean, onSync: () -> Unit) {
    val ctx = LocalContext.current
    Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(Icons.Default.OfflinePin, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
        val text = when {
            syncing -> stringResource(R.string.files_offline_syncing)
            root?.error != null -> stringResource(R.string.files_offline_error, root.error)
            (root?.lastSync ?: 0) > 0 -> stringResource(R.string.files_offline_synced, Fmt.dateTime(ctx, root!!.lastSync, false))
            else -> stringResource(R.string.files_offline_never)
        }
        Text(text, style = MaterialTheme.typography.bodySmall, color = if (root?.error != null && !syncing) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.weight(1f).padding(start = 8.dp))
        TextButton(onClick = onSync, enabled = !syncing) { Text(stringResource(R.string.files_sync_now)) }
    }
}

/** Belegung wie im Web: mit Kontingent als Balken, sonst nur die Menge. */
@Composable
private fun UsageBar(usage: Long, quota: Long) {
    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) {
        if (quota > 0) {
            val frac = (usage.toFloat() / quota).coerceIn(0f, 1f)
            LinearProgressIndicator(progress = { frac }, modifier = Modifier.fillMaxWidth(),
                color = if (frac > 0.9f) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary)
            Text(stringResource(R.string.files_usage_quota, sizeText(usage), sizeText(quota)), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp))
        } else Text(stringResource(R.string.files_usage, sizeText(usage)), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun NameDialog(title: String, initial: String, onDismiss: () -> Unit, onOk: (String) -> Unit) {
    var name by remember { mutableStateOf(initial) }
    val valid = name.isNotBlank() && !name.contains('/') && !name.contains('\\') && name.trim() != "." && name.trim() != ".."
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = { OutlinedTextField(value = name, onValueChange = { name = it }, singleLine = true, label = { Text(stringResource(R.string.files_name)) }) },
        confirmButton = { TextButton(onClick = { onOk(name.trim()) }, enabled = valid) { Text(stringResource(R.string.save)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

/**
 * Zielordner waehlen (Verschieben, Kopieren, Ziel des automatischen Hochladens) als Vollbild-Seite:
 * Pfad als Brotkrumen, Unterordner als Liste, "Neuer Ordner" legt an und wechselt gleich hinein.
 * [exclude] ist der verschobene Ordner selbst (dort hinein geht es nicht). Gibt es [start] nicht (mehr),
 * beginnt die Auswahl beim naechsten vorhandenen Ordner darueber.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun FolderPickerDialog(start: String, exclude: String?, onDismiss: () -> Unit, title: String = stringResource(R.string.files_move), confirm: String = stringResource(R.string.files_move_here), onPick: (String) -> Unit) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).files
    val scope = rememberCoroutineScope()
    var dir by remember { mutableStateOf(start) }
    var folders by remember { mutableStateOf<List<FileItem>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var newFolder by remember { mutableStateOf(false) }
    var tick by remember { mutableIntStateOf(0) }
    LaunchedEffect(dir, tick) {
        folders = null
        try {
            folders = withContext(Dispatchers.IO) { repo.listServer(dir) }.items.filter { it.isDir && it.path != exclude }.sortedBy { it.name.lowercase() }
            error = null
        } catch (e: at.dimes.dmoffice.api.ApiException) {
            // Ordner gibt es nicht (mehr): eine Ebene hoeher anfangen
            if (e.code == 404 && dir.isNotEmpty()) dir = FilesRepo.parent(dir) else error = e.message ?: e.toString()
        } catch (e: Exception) { error = e.message ?: e.toString() }
    }
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss, properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        // Kopf, Liste mit dem restlichen Platz, Fussleiste mit "Hierhin" - als Spalte, damit die Fussleiste sicher sichtbar ist
        androidx.compose.material3.Surface(Modifier.fillMaxSize()) {
            Column(Modifier.fillMaxSize().systemBarsPadding()) {
                TopAppBar(title = { Text(title) }, windowInsets = androidx.compose.foundation.layout.WindowInsets(0),
                    navigationIcon = { IconButton(onClick = onDismiss) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } },
                    actions = {
                        IconButton(onClick = { newFolder = true }) { Icon(Icons.Default.CreateNewFolder, contentDescription = stringResource(R.string.files_new_folder)) }
                        // Bestaetigen oben: im Dialogfenster ist der untere Rand nicht auf allen Geraeten sichtbar
                        androidx.compose.material3.Button(onClick = { onPick(dir) }, enabled = dir != exclude, modifier = Modifier.padding(end = 8.dp)) { Text(confirm) }
                    })
                Breadcrumb(dir) { dir = it }
                HorizontalDivider()
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(16.dp)) }
                val list = folders
                Box(Modifier.weight(1f).fillMaxWidth()) {
                    when {
                        list == null && error == null -> CircularProgressIndicator(Modifier.align(Alignment.Center))
                        list != null && list.isEmpty() -> Column(Modifier.fillMaxWidth().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                            Icon(Icons.Outlined.Folder, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(48.dp))
                            Text(stringResource(R.string.files_no_subfolders), color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 8.dp))
                            TextButton(onClick = { newFolder = true }) { Text(stringResource(R.string.files_new_folder)) }
                        }
                        list != null -> LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 96.dp)) {
                            items(list, key = { it.path }) { f ->
                                ListItem(headlineContent = { Text(f.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                                    leadingContent = { Icon(Icons.Default.Folder, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                                    trailingContent = { Icon(Icons.Default.ChevronRight, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant) },
                                    modifier = Modifier.clickable { dir = f.path })
                            }
                        }
                    }
                }
            }
        }
        if (newFolder) NameDialog(stringResource(R.string.files_new_folder), "", onDismiss = { newFolder = false }) { name ->
            newFolder = false
            val path = FilesRepo.join(dir, name)
            scope.launch {
                try { withContext(Dispatchers.IO) { repo.mkdir(path) }; dir = path; tick++ }
                catch (e: Exception) { error = e.message ?: e.toString() }
            }
        }
    }
}

/** Geladene Datei mit der passenden App oeffnen bzw. teilen (FileProvider, nur Leserecht). */
internal fun openLocalFile(ctx: Context, f: File, item: FileItem, share: Boolean) {
    val uri = FileProvider.getUriForFile(ctx, ctx.packageName + ".files", f)
    val i = if (share) Intent.createChooser(Intent(Intent.ACTION_SEND).setType(item.type).putExtra(Intent.EXTRA_STREAM, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), item.name)
    else Intent(Intent.ACTION_VIEW).setDataAndType(uri, item.type).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    try { ctx.startActivity(i) }
    catch (_: ActivityNotFoundException) {
        // Keine App fuer diesen Typ: Auswahl ueber alle, die irgendetwas oeffnen
        try { ctx.startActivity(Intent.createChooser(Intent(Intent.ACTION_VIEW).setDataAndType(uri, "*/*").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), item.name)) }
        catch (_: ActivityNotFoundException) { android.widget.Toast.makeText(ctx, ctx.getString(R.string.files_no_app), android.widget.Toast.LENGTH_LONG).show() }
    }
}

/** Mehrere Dateien auf einmal teilen. */
private fun shareMany(ctx: Context, files: List<Pair<FileItem, File>>) {
    val uris = ArrayList(files.map { FileProvider.getUriForFile(ctx, ctx.packageName + ".files", it.second) })
    val i = Intent(Intent.ACTION_SEND_MULTIPLE).setType("*/*").putParcelableArrayListExtra(Intent.EXTRA_STREAM, uris).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    ctx.startActivity(Intent.createChooser(i, null))
}

