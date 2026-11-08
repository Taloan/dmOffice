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
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.RadioButton
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.material.icons.filled.UnfoldMore
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
import androidx.compose.material.icons.automirrored.filled.Chat
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Place
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material.icons.filled.Book
import androidx.compose.material.icons.filled.Map
import androidx.compose.material.icons.filled.Face
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material3.LocalContentColor
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.material.icons.filled.People
import androidx.compose.material.icons.filled.PhotoLibrary
import androidx.compose.material.icons.filled.Share
import androidx.compose.material.icons.filled.RemoveCircleOutline
import androidx.compose.material.icons.filled.SelectAll
import androidx.compose.material3.AssistChip
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.InsertDriveFile
import androidx.compose.material.icons.automirrored.filled.Sort
import androidx.compose.material.icons.automirrored.filled.ViewList
import androidx.compose.material.icons.filled.HideImage
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
import androidx.compose.material.icons.filled.Download
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
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.LockOpen
import androidx.compose.material.icons.outlined.Category
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
import androidx.lifecycle.repeatOnLifecycle
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
import at.dimes.dmoffice.files.TimelineGroup
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

/** Bereiche wie im Web (linke Leiste dort, hier eine Chip-Zeile); Fotos ist der eigene Reiter „Galerie“. */
private enum class Area(val label: Int, val icon: ImageVector) {
    Files(R.string.files_area_files, Icons.Outlined.Folder),
    Recent(R.string.files_area_recent, Icons.Outlined.History),
    Favorites(R.string.files_area_favorites, Icons.Outlined.StarOutline),
    /** Kategorien und Tags als virtuelle Ordner (docs/datei-kategorien-konzept.md) */
    Categories(R.string.files_area_categories, Icons.Outlined.Category),
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
    /** Mit Personen teilen - eine Datei bzw. ein Ordner, oder mehrere (dann kommen die Gewählten bei jedem dazu) */
    data class People(val items: List<FileItem>) : FileAction()
    data class Versions(val item: FileItem) : FileAction()
    data class Details(val item: FileItem) : FileAction()
    data class DeleteLink(val link: FileLink) : FileAction()
    data class Purge(val ids: List<Long>) : FileAction()
    data object EmptyTrash : FileAction()
    data class Labels(val items: List<FileItem>) : FileAction()
    data class Info(val items: List<FileItem>) : FileAction()
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
fun FilesScreen(onClose: (() -> Unit)?, onAutoUpload: (() -> Unit)? = null, gallery: Boolean = false) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).files
    val ui = remember { FilesUi(ctx) }
    val offState by repo.offline.state.collectAsState()
    val syncingRoots by repo.offline.syncing.collectAsState()
    val scope = rememberCoroutineScope()
    val snackbar = remember { SnackbarHostState() }
    var area by rememberSaveable { mutableStateOf(if (gallery) Area.Photos else Area.Files) }
    var path by rememberSaveable { mutableStateOf("") }
    var listing by remember { mutableStateOf<Listing?>(null) }
    // Galerie: zuletzt Geladenes sofort zeigen, im Hintergrund auffrischen
    var items by remember { mutableStateOf<List<FileItem>?>(if (gallery) repo.photosCache else null) }
    // Alben der Galerie: Liste, gewaehltes Album (null: alle Fotos), eigene Fotos darin, offener Dialog
    var albums by remember { mutableStateOf<List<at.dimes.dmoffice.files.AlbumInfo>>(emptyList()) }
    var album by remember { mutableStateOf<Long?>(null) }
    var albumMine by remember { mutableStateOf<Set<String>>(emptySet()) }
    var albumAsk by remember { mutableStateOf<AlbumAsk?>(null) }
    // Galerie: Aufnahmedaten, Gruppierung, Filter (je Eigenschaft gewaehlte Werte)
    var photoMeta by remember { mutableStateOf(repo.photoMetaCache) }
    // Nach Quelle, Kategorie oder Tag: Gruppen vom Server (Name je Foto, Reihenfolge der Gruppen)
    var serverGroups by remember { mutableStateOf<List<TimelineGroup>?>(null) }
    var photoGroup by remember { mutableStateOf(App.get(ctx).prefs.photoGroup) }
    var photoFit by remember { mutableStateOf(App.get(ctx).prefs.photoFit) }
    var photoFilter by remember { mutableStateOf<Map<String, Set<String>>>(emptyMap()) }
    var photoOptions by remember { mutableStateOf(false) }
    // Markierte Fotos ins Tagebuch: Auswahl des Tagebuchs, wenn es mehrere gibt
    var journalPick by remember { mutableStateOf<Pair<List<String>, List<at.dimes.dmoffice.journal.Journal>>?>(null) }
    // Galerie als Karte (Atlas) statt Kacheln
    var atlas by rememberSaveable { mutableStateOf(false) }
    // „Tresor herunterladen“ (nur im Tresor)
    var tresorDownload by remember { mutableStateOf(false) }
    // Tresor ohne Netz: Offline-Kopie mit dem Tresor-Passwort öffnen
    var tresorUnlock by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) { withContext(Dispatchers.IO) { at.dimes.dmoffice.files.TresorOffline.init(ctx) } }
    val tresorOffline by at.dimes.dmoffice.files.TresorOffline.roots.collectAsState()
    if (tresorDownload) TresorDownloadDialog { tresorDownload = false }
    // Personen (Gesichter): nur wenn der Server die Photo-Engine dafür hat
    var people by rememberSaveable { mutableStateOf(false) }
    var facesAvailable by remember { mutableStateOf(false) }
    // Aus dem Atlas: Galerie nur mit den Fotos eines angetippten Kreises (Pfade); null = kein Ortsfilter
    var placeFilter by remember { mutableStateOf<Set<String>?>(null) }
    var photoOldest by remember { mutableStateOf(App.get(ctx).prefs.photoOldest) }
    var used by remember { mutableStateOf<Map<String, Long>>(emptyMap()) }
    var links by remember { mutableStateOf<List<FileLink>?>(null) }
    var withMe by remember { mutableStateOf<List<FilesRepo.SharedMount>>(emptyList()) }
    var trash by remember { mutableStateOf<TrashList?>(null) }
    // Kategorien: geöffnete Kategorie bzw. Tag (beides null: oberste Ebene), Baum, Zuordnungen im virtuellen Ordner
    var catId by rememberSaveable { mutableStateOf<String?>(null) }
    var tagSel by rememberSaveable { mutableStateOf<String?>(null) }
    var catTree by remember { mutableStateOf<at.dimes.dmoffice.files.CatTree?>(null) }
    var catLabels by remember { mutableStateOf<Map<String, at.dimes.dmoffice.files.FileLabels>>(emptyMap()) }
    var onlyAuto by remember { mutableStateOf(false) }
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
    // Ansicht Fotos: Ordner des gewaehlten Bildes (oder einen darueber) ausschliessen
    var excludeFrom by remember { mutableStateOf<String?>(null) }
    var trashSel by remember { mutableStateOf<Set<Long>>(emptySet()) }
    var viewer by remember { mutableStateOf<Pair<List<FileItem>, Int>?>(null) }
    // Scrollstand der Kacheln hier statt im Raster: solange ein Bild offen ist, ist das Raster nicht da - sein eigener Stand
    // ginge verloren, und nach dem Zurückgehen stünde die Galerie wieder am Anfang
    val gridState = androidx.compose.runtime.saveable.rememberSaveable(area, path, saver = androidx.compose.foundation.lazy.grid.LazyGridState.Saver) { androidx.compose.foundation.lazy.grid.LazyGridState() }
    // Office-Server (Editor in OfficeActivity); null = noch nicht gefragt
    var office by remember { mutableStateOf<OfficeConfig?>(null) }
    val textUploading = stringResource(R.string.files_uploading)
    val textLoading = stringResource(R.string.files_loading)
    val textSyncing = stringResource(R.string.files_offline_syncing)
    val textUndo = stringResource(R.string.files_undo)

    val searching = searchOpen && (query.isNotBlank() || kind != null)

    /** Inhalt des aktuellen Bereichs laden; Ordner zuerst aus dem Speicher bzw. Room (sofort), dann frisch. */
    fun reload(clearError: Boolean = false) { tick++; if (clearError) error = null }

    LaunchedEffect(area, photoGroup, tick) {
        serverGroups = if (area == Area.Photos && photoGroup in SERVER_GROUPS)
            try { withContext(Dispatchers.IO) { repo.photoGroups(photoGroup) } } catch (_: Exception) { null } else null
    }
    LaunchedEffect(area) {
        if (area == Area.Photos) facesAvailable = try { withContext(Dispatchers.IO) { repo.facesStatus()?.available == true } } catch (_: Exception) { false }
    }
    LaunchedEffect(area, path, tick, album, catId, tagSel) {
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
                Area.Photos -> {
                    albums = try { withContext(Dispatchers.IO) { repo.albums() } } catch (_: Exception) { albums }
                    val id = album?.takeIf { a -> albums.any { it.id == a } }
                    if (id != album) album = id
                    if (id != null) { val (l, mine) = withContext(Dispatchers.IO) { repo.albumItems(id) }; albumMine = mine; items = l }
                    else items = withContext(Dispatchers.IO) { repo.photos() }
                    photoMeta = try { withContext(Dispatchers.IO) { repo.photoMeta() } } catch (_: Exception) { photoMeta }
                }
                Area.Shared -> {
                    withMe = try { withContext(Dispatchers.IO) { repo.sharedWithMe() } } catch (_: Exception) { withMe }
                    links = withContext(Dispatchers.IO) { repo.links() }
                }
                Area.Trash -> trash = withContext(Dispatchers.IO) { repo.trash() }
                Area.Categories -> {
                    catTree = withContext(Dispatchers.IO) { repo.categories() }
                    val c = catId
                    val g = tagSel
                    val r = withContext(Dispatchers.IO) { if (c != null) repo.categoryItems(c) else if (g != null) repo.tagItems(g) else null }
                    catLabels = r?.labels ?: emptyMap()
                    items = r?.items ?: emptyList()
                }
            }
            error = null
        } catch (_: at.dimes.dmoffice.files.TresorOffline.NeedPassword) { tresorUnlock = true; error = null }
        catch (e: Exception) { error = e.message ?: e.toString() }
        finally { loading = false }
    }
    if (tresorUnlock) TresorOfflineUnlock(onDismiss = { tresorUnlock = false }) { tresorUnlock = false; reload(clearError = true) }
    LaunchedEffect(tick) { office = withContext(Dispatchers.IO) { repo.officeConfig() } }
    // Galerie aktuell halten: nach jedem Durchgang des automatischen Hochladens (neues Foto vom Handy) und beim Zurückkommen in die App
    val uploading by at.dimes.dmoffice.upload.AutoUpload.running.collectAsState()
    var wasUploading by remember { mutableStateOf(false) }
    LaunchedEffect(uploading) { if (wasUploading && !uploading && area == Area.Photos) reload(); wasUploading = uploading }
    val lifecycleOwner = androidx.lifecycle.compose.LocalLifecycleOwner.current
    LaunchedEffect(lifecycleOwner) {
        var first = true
        lifecycleOwner.lifecycle.repeatOnLifecycle(androidx.lifecycle.Lifecycle.State.RESUMED) { if (!first && area == Area.Photos) reload(); first = false }
    }
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
    // Galerie (eigener Reiter): nie in die Ordner wechseln - Zurück schließt Karte, Personen, Auswahl, Suche, sonst geht es an den Hauptbildschirm
    BackHandler(enabled = atlas || people || selected.isNotEmpty() || trashSel.isNotEmpty() || searchOpen || (area == Area.Files && path.isNotEmpty()) || (area != Area.Files && !gallery)) {
        when {
            // Karte bzw. Personen: zurück in die Galerie an dieselbe Stelle, die Auswahl bleibt
            atlas && area == Area.Photos -> atlas = false
            people && area == Area.Photos -> people = false
            selected.isNotEmpty() || trashSel.isNotEmpty() -> { selected = emptySet(); trashSel = emptySet() }
            searchOpen -> { searchOpen = false; query = ""; kind = null }
            area == Area.Categories && tagSel != null -> tagSel = null
            area == Area.Categories && catId != null -> { catId = catTree?.cats?.firstOrNull { it.id == catId }?.parent; onlyAuto = false }
            area != Area.Files && !gallery -> area = Area.Files
            area == Area.Files -> path = FilesRepo.parent(path)
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

    val textShown = stringResource(R.string.files_shown_only)
    // „Im Chat …“ (Version 2)
    var sendChat by remember { mutableStateOf<List<at.dimes.dmoffice.chat.ChatOutRef>?>(null) }
    // Teilen: erst die Wege in dmOffice (Chat, Tagebuch, Personen, Album, Link), dann andere Apps
    var shareSheet by remember { mutableStateOf<List<FileItem>?>(null) }

    /** Markierte Fotos ins Tagebuch - nur eigene Dateien (Album-Einträge und Galerie-Ordner liegen woanders); JPG+RAW: das JPG. */
    fun toJournal(items: List<FileItem>) {
        val paths = items.filter { !FilesRepo.isAlbum(it.path) && !it.path.startsWith("ext:") && !it.isDir }.map { it.path }
        if (paths.isEmpty()) { error = ctx.getString(R.string.journal_from_files_none); return }
        scope.launch {
            try {
                val list = App.get(ctx).journal.journals().filter { it.canWrite }
                if (list.size == 1) {
                    val (entries, photos) = App.get(ctx).journal.fromFiles(list[0].id, paths)
                    selected = emptySet()
                    android.widget.Toast.makeText(ctx, ctx.resources.getQuantityString(R.plurals.share_done_journal, entries, entries, photos), android.widget.Toast.LENGTH_LONG).show()
                } else if (list.isEmpty()) error = ctx.getString(R.string.journal_from_files_nojournal)
                else journalPick = paths to list
            } catch (e: Exception) { error = e.message }
        }
    }
    val textSent = stringResource(R.string.chat_sent_done)
    sendChat?.let { refs -> SendToChatDialog(refs, onDismiss = { sendChat = null }) { err -> sendChat = null; if (err != null) error = err else scope.launch { snackbar.showSnackbar(textSent) } } }
    /** In einer App oeffnen bzw. teilen (Datei vorher in den Cache). Teilen: Fotos auf Wunsch verkleinert (small null = Einstellung). */
    fun external(list: List<FileItem>, share: Boolean, small: Boolean? = null) {
        // Nur gezeigt (Version 2): nicht an andere Apps geben
        if (list.any { repo.isShownCached(it.path) }) { error = textShown; return }
        scope.launch {
            busy = textLoading
            try {
                val shrink = share && (small ?: App.get(ctx).prefs.shareSmall)
                val files = withContext(Dispatchers.IO) {
                    list.filter { !it.isDir }.map { item ->
                        val copy = if (shrink) repo.shareCopy(item) else null
                        if (copy != null) item.copy(name = copy.name, mime = "image/jpeg") to copy else item to repo.cacheFile(item)
                    }
                }
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
    // Im virtuellen Ordner einer Kategorie: automatisch eingeordnet (noch nicht bestätigt) in ihr oder darunter
    val catBelow = catId?.let { id -> catTree?.below(id) } ?: emptySet()
    fun autoIn(i: FileItem) = catLabels[i.path]?.cats?.filter { it.auto && it.id in catBelow }?.map { it.id } ?: emptyList()
    val shown: List<FileItem> = when {
        !searching && area == Area.Recent -> baseItems
        !searching && area == Area.Categories && onlyAuto -> FileViews.sort(baseItems.filter { autoIn(it).isNotEmpty() }, ui.sort, ui.desc)
        !searching && area == Area.Photos -> FileViews.stack(baseItems).filter { PhotoFilters.passes(it, photoMeta[it.path], photoFilter) && placeFilter?.contains(it.path) != false }
        else -> FileViews.sort(baseItems, ui.sort, ui.desc)
    }
    val groups = when {
        !searching && area == Area.Photos && photoGroup in SERVER_GROUPS -> serverGroups?.let { sg ->
            val at = HashMap<String, Int>(); sg.forEachIndexed { n, g -> g.i.forEach { at.putIfAbsent(it, n) } }
            val dated = FileViews.byDate(shown, "none", { photoMeta[it.path]?.taken ?: it.modifiedMs }, oldest = photoOldest).flatMap { it.items }
            dated.groupBy { at[it.path] ?: sg.size }.toSortedMap().map { (n, v) -> FileViews.Group(SERVER_GROUP + (sg.getOrNull(n)?.l ?: ""), v) }
        } ?: listOf(FileViews.Group("", shown))
        !searching && area == Area.Photos -> FileViews.byDate(shown, photoGroup, { photoMeta[it.path]?.taken ?: it.modifiedMs }, oldest = photoOldest)
        !searching && area == Area.Recent -> listOf(FileViews.Group("", shown))
        else -> FileViews.group(shown, ui.group)
    }
    val showTiles = (ui.tiles || area == Area.Photos) && !(searching && contentMode)
    val selItems = shown.filter { it.path in selected }
    val showPath = searching || area == Area.Recent || area == Area.Favorites || area == Area.Categories

    viewer?.let { (list, start) ->
        val shownList = list.any { repo.isShownCached(it.path) }
        FileViewer(list, start, onClose = { viewer = null }, onShare = if (shownList) null else ({ item, small -> external(listOf(item), share = true, small = small) }),
            onOpenExternal = if (shownList) null else ({ external(listOf(it), share = false) }), secure = shownList,
            locationOf = { item -> photoMeta[item.path]?.let { m -> if (m.lat != null && m.lon != null) m.lat to m.lon else null } },
            infoOf = if (area == Area.Photos) ({ item -> photoMeta[item.path] }) else null, onInfoSaved = { reload() })
        return
    }

    // Als Reiter (onClose == null): ohne Zurueck-Pfeil, Systemleisten beruecksichtigt schon der Hauptbildschirm
    val insets = if (onClose == null) androidx.compose.foundation.layout.WindowInsets(0) else null
    albumAsk?.let { ask ->
        when (ask) {
            is AlbumAsk.Pick -> AlbumPickDialog(albums.filter { it.fixed }, onDismiss = { albumAsk = null },
                onNew = { albumAsk = AlbumAsk.New(ask.paths) }) { id -> albumAsk = null; selected = emptySet(); run(null) { repo.addToAlbum(id, ask.paths) } }
            is AlbumAsk.New -> NameDialog(stringResource(R.string.album_new), "", onDismiss = { albumAsk = null }) { name ->
                albumAsk = null; selected = emptySet()
                run(null) { val id = repo.createAlbum(name); if (ask.paths.isNotEmpty()) repo.addToAlbum(id, ask.paths) else album = id }
            }
            AlbumAsk.Rename -> NameDialog(stringResource(R.string.album_rename), albums.firstOrNull { it.id == album }?.name ?: "", onDismiss = { albumAsk = null }) { name ->
                albumAsk = null; val id = album ?: return@NameDialog; run(null) { repo.renameAlbum(id, name) }
            }
        }
    }
    Scaffold(
        contentWindowInsets = insets ?: androidx.compose.material3.ScaffoldDefaults.contentWindowInsets,
        snackbarHost = { SnackbarHost(snackbar) },
        topBar = {
            when {
                selected.isNotEmpty() && selItems.any { FilesRepo.isAlbum(it.path) } -> AlbumSelectionBar(selItems,
                    canRemove = albums.firstOrNull { it.id == album }?.let { a -> a.fixed && (a.mine || selItems.all { it.path in albumMine }) } == true,
                    onClear = { selected = emptySet() }, onSelectAll = { selected = shown.map { it.path }.toSet() },
                    onShare = { shareSheet = selItems },
                    onRemove = { val id = album ?: return@AlbumSelectionBar; val l = selItems; selected = emptySet(); run(null) { repo.removeFromAlbum(id, l) } }, insets = insets)
                // Auf der Karte mit Auswahl: „X“ führt zurück in die Galerie, die Auswahl bleibt
                selected.isNotEmpty() -> SelectionBar(selItems, photos = area == Area.Photos && !searching, onClear = { if (atlas && area == Area.Photos) atlas = false else selected = emptySet() },
                    onSelectAll = { selected = shown.map { it.path }.toSet() },
                    onAction = { a ->
                        val one = selItems.singleOrNull()
                        when (a) {
                            "link" -> one?.let { action = FileAction.Link(it) }
                            "people" -> action = FileAction.People(selItems)
                            "fav" -> favorite(selItems, !selItems.all { it.favorite })
                            "move" -> action = FileAction.Transfer(selItems, copy = false)
                            "copy" -> if (selItems.any { repo.isShownCached(it.path) }) error = textShown else action = FileAction.Transfer(selItems, copy = true)
                            "delete" -> action = FileAction.Delete(selItems)
                            "rename" -> one?.let { action = FileAction.Rename(it) }
                            "versions" -> one?.let { action = FileAction.Versions(it) }
                            "details" -> one?.let { action = FileAction.Details(it) }
                            "exclude" -> one?.let { excludeFrom = it.path }
                            "labels" -> action = FileAction.Labels(selItems.filter { !it.isDir })
                            "info" -> action = FileAction.Info(selItems)
                            "share" -> external(selItems, share = true)
                            "album" -> albumAsk = AlbumAsk.Pick(selItems.map { it.path })
                            "journal" -> toJournal(selItems)
                            "sheet" -> shareSheet = selItems
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
                else -> TopAppBar(title = { Text(stringResource(if (gallery) R.string.gallery else if (area == Area.Files) R.string.files else area.label)) },
                    windowInsets = insets ?: androidx.compose.material3.TopAppBarDefaults.windowInsets,
                    navigationIcon = { if (onClose != null) IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
                    actions = {
                        if (area != Area.Shared && area != Area.Trash) IconButton(onClick = { searchOpen = true }) { Icon(Icons.Default.Search, contentDescription = stringResource(R.string.files_search)) }
                        if (area != Area.Shared && area != Area.Trash && area != Area.Photos)
                            IconButton(onClick = { ui.useTiles(!ui.tiles) }) { Icon(if (ui.tiles) Icons.AutoMirrored.Filled.ViewList else Icons.Default.GridView, contentDescription = stringResource(if (ui.tiles) R.string.files_view_details else R.string.files_view_tiles)) }
                        if (area == Area.Photos && !searching) PhotoViewMenu(photoOldest, photoGroup,
                            onOldest = { photoOldest = it; App.get(ctx).prefs.photoOldest = it }, onGroup = { photoGroup = it; App.get(ctx).prefs.photoGroup = it },
                            fit = photoFit, onFit = { photoFit = it; App.get(ctx).prefs.photoFit = it })
                        else if (area != Area.Shared && area != Area.Trash) ViewMenu(ui, tilesActive = ui.tiles || area == Area.Photos, sortable = area != Area.Recent && area != Area.Photos)
                        MoreMenu(area, onNewFolder = if (path == "Chat" || path.startsWith("Chat/")) null else ({ action = FileAction.NewFolder }), onAutoUpload = onAutoUpload, onRefresh = { reload(clearError = true) },
                            onEmptyTrash = { action = FileAction.EmptyTrash }, onExcludes = if (area == Area.Photos) ({ excludeFrom = "" }) else null,
                            onTresorDownload = if (area == Area.Files && FilesRepo.isTresor(path)) ({ tresorDownload = true }) else null)
                    })
            }
        },
        // Belegung unten als eigene Leiste, damit der Knopf "Hochladen" sie nicht verdeckt
        bottomBar = { if (area == Area.Files && !searching) listing?.let { Box(Modifier.navigationBarsPadding()) { UsageBar(if (it.quota > 0 && it.used > 0) it.used else it.usage, it.quota) } } },
        floatingActionButton = {
            // Chat-Ordner: dort landet nur, was im Chat mit „Speichern“ abgelegt wird - kein Hochladen oder Anlegen
            val inChat = path == "Chat" || path.startsWith("Chat/")
            if (area == Area.Files && !searching && selected.isEmpty() && !inChat) Box {
                AddButton(stringResource(R.string.files_upload)) { addMenu = true }
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
            // Leisten bleiben beim Markieren stehen - sonst rutscht das Raster nach oben und andere Fotos unter den Finger
            else if (!gallery && trashSel.isEmpty()) AreaChips(area, { a -> area = a; selected = emptySet(); trashSel = emptySet() },
                // Tresor in der Dateiansicht (docs/tresor-cryptomator-konzept.md, 3a): geschlossen fragt der Dialog beim Laden
                onTresor = if (!at.dimes.dmoffice.data.Features.on("vault")) null
                    else ({ area = Area.Files; path = FilesRepo.TRESOR; selected = emptySet(); searchOpen = false }))
            // Galerie: Alben zur Auswahl, dazu Neu und das Menue des gewaehlten
            if (area == Area.Photos && !searching) AlbumBar(albums, album, onPick = { album = it; selected = emptySet(); placeFilter = null },
                onNew = { albumAsk = AlbumAsk.New(emptyList()) }, onRename = { albumAsk = AlbumAsk.Rename },
                onDelete = { val id = album ?: return@AlbumBar; album = null; run(null) { repo.deleteAlbum(id) } },
                filtered = photoFilter.values.any { it.isNotEmpty() } || placeFilter != null, onOptions = { photoOptions = true }, atlas = atlas,
                onAtlas = { atlas = !atlas; if (atlas) { placeFilter = null; people = false } },
                people = people, onPeople = if (facesAvailable) ({ people = !people; if (people) atlas = false }) else null)
            placeFilter?.let { pf ->
                if (area == Area.Photos && !searching && !atlas) Row(Modifier.padding(horizontal = 12.dp)) {
                    androidx.compose.material3.InputChip(selected = true, onClick = { placeFilter = null },
                        label = { Text(stringResource(R.string.photos_atlas_place, pf.size)) },
                        leadingIcon = { Icon(Icons.Default.Place, contentDescription = null, Modifier.size(18.dp)) },
                        trailingIcon = { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.close), Modifier.size(18.dp)) })
                }
            }
            if (photoOptions) PhotoOptionsSheet(FileViews.stack(baseItems), photoMeta, photoGroup, photoFilter,
                onGroup = { photoGroup = it; App.get(ctx).prefs.photoGroup = it }, onFilter = { photoFilter = it }, onDismiss = { photoOptions = false })
            if (area == Area.Files && !searching) {
                Breadcrumb(path) { path = it }
                if (listing?.cached == true && curRoot == null) Text(stringResource(R.string.files_cached_state), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.tertiary, modifier = Modifier.padding(horizontal = 16.dp, vertical = 2.dp))
                if (curRoot != null) OfflineBanner(offState.roots[curRoot], curRoot in syncingRoots) { val p = path; run(textSyncing) { repo.offline.sync(p) } }
            }
            if (area == Area.Categories && !searching && (catId != null || tagSel != null)) {
                CategoryCrumb(catTree, catId, tagSel) { id -> tagSel = null; catId = id; onlyAuto = false }
                catId?.let { c ->
                    SubCategoryChips(catTree, c) { catId = it; onlyAuto = false }
                    UnconfirmedBar(baseItems.count { autoIn(it).isNotEmpty() }, onlyAuto, onToggle = { onlyAuto = !onlyAuto }, onConfirmAll = {
                        val todo = baseItems.map { it.path to autoIn(it) }.filter { it.second.isNotEmpty() }
                        onlyAuto = false
                        run(null) { for ((p, ids) in todo) repo.setLabels(at.dimes.dmoffice.files.LabelsSet(listOf(p), add = ids)) }
                    })
                }
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
                        if (l != null && l.isEmpty() && withMe.isEmpty()) Empty(stringResource(R.string.files_shared_empty))
                        LazyColumn(Modifier.fillMaxSize()) {
                            // Was andere mit mir teilen - antippen oeffnet den Ordner in der Ablage
                            if (withMe.isNotEmpty()) {
                                item(key = "withme") { Text(stringResource(R.string.files_shared_with_me), style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(start = 16.dp, top = 12.dp, bottom = 4.dp)) }
                                items(withMe, key = { "m:" + it.path }) { m ->
                                    androidx.compose.material3.ListItem(
                                        leadingContent = { Icon(Icons.Outlined.Folder, null) },
                                        headlineContent = { Text(m.name.ifEmpty { m.path.substringAfterLast('/') }, maxLines = 1) },
                                        supportingContent = { Text(stringResource(R.string.files_shared_by, m.owner) + when { m.showOnly -> " · " + stringResource(R.string.files_shared_show_only); !m.canWrite -> " · " + stringResource(R.string.files_shared_read_only); else -> "" }, maxLines = 1) },
                                        modifier = Modifier.clickable { path = m.path; area = Area.Files })
                                    HorizontalDivider()
                                }
                                if (!l.isNullOrEmpty()) item(key = "mylinks") { Text(stringResource(R.string.files_shared_links), style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(start = 16.dp, top = 16.dp, bottom = 4.dp)) }
                            }
                            items(l ?: emptyList(), key = { it.id }) { link ->
                                LinkRow(link, onEdit = { link.item?.let { action = FileAction.Link(it, link) } }, onDelete = { action = FileAction.DeleteLink(link) })
                                HorizontalDivider()
                            }
                        }
                    }
                    !searching && area == Area.Categories && catId == null && tagSel == null ->
                        CategoryRoot(catTree, onOpen = { catId = it; onlyAuto = false }, onTag = { tagSel = it }, onChanged = { reload() }, onError = { error = it })
                    !searching && area == Area.Trash -> TrashView(trash, trashSel, onToggle = { id -> trashSel = if (id in trashSel) trashSel - id else trashSel + id },
                        onRestore = { id -> run(null) { repo.restore(listOf(id)) } })
                    else -> {
                        val emptyText = when {
                            searching -> stringResource(R.string.files_search_none)
                            area == Area.Favorites -> stringResource(R.string.files_favorites_empty)
                            area == Area.Photos -> stringResource(R.string.files_photos_empty)
                            area == Area.Recent -> stringResource(R.string.files_recent_empty)
                            area == Area.Categories && tagSel != null -> stringResource(R.string.cats_tag_empty)
                            area == Area.Categories -> stringResource(R.string.cats_files_empty)
                            area == Area.Files && (path == "Chat" || path.startsWith("Chat/")) -> stringResource(R.string.files_chat_empty)
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
                                "share" -> shareSheet = listOf(item)
                                "save" -> if (repo.isShownCached(item.path)) error = textShown else { saveItem = item; saveTo.launch(item.name) }
                                "link" -> action = FileAction.Link(item)
                                "people" -> action = FileAction.People(listOf(item))
                                "fav" -> favorite(listOf(item), !item.favorite)
                                "rename" -> action = FileAction.Rename(item)
                                "move" -> action = FileAction.Transfer(listOf(item), copy = false)
                                "copy" -> if (repo.isShownCached(item.path)) error = textShown else action = FileAction.Transfer(listOf(item), copy = true)
                                "versions" -> action = FileAction.Versions(item)
                                "details" -> action = FileAction.Details(item)
                                "labels" -> action = FileAction.Labels(listOf(item))
                                "select" -> selected = selected + item.path
                                "chat" -> if (item.fileId > 0) sendChat = listOf(at.dimes.dmoffice.chat.ChatOutRef(if (item.isDir) "folder" else "file", fileId = item.fileId))
                                "offline" -> if (FilesRepo.isTresor(item.path)) run(textSyncing) { repo.tresorOffline(item.path, item.path !in tresorOffline) }
                                    else if (offState.roots.containsKey(item.path)) run(null) { repo.offline.unmark(item.path) }
                                    else run(textSyncing) { repo.offline.mark(item.path); repo.offline.sync(item.path) }
                                "delete" -> action = FileAction.Delete(listOf(item))
                            }
                        }
                        val offlineInfo: (FileItem) -> Pair<Boolean, Boolean> = { item ->
                            // Im Tresor: eigene Offline-Kopie, verschlüsselt (TresorOffline)
                            val isRoot = item.isDir && (offState.roots.containsKey(item.path) || item.path in tresorOffline)
                            isRoot to (item.isDir && area == Area.Files && !searching && curRoot == null && !isRoot && !repo.isShownCached(item.path))
                        }
                        // Neue Liste (Ordner, Bereich, Sortierung, Gruppen): oben anfangen, sonst bleibt eine Gruppenueberschrift weggescrollt
                        androidx.compose.runtime.key(area, path, ui.sort, ui.desc, ui.group, searching) {
                        if (area == Area.Photos && !searching && people && facesAvailable) GalleryPeople(onOpen = { i, l -> open(i, l) })
                        // Mit Auswahl: nur die markierten Fotos auf der Karte
                        else if (area == Area.Photos && !searching && atlas) PhotoAtlas(if (selected.isEmpty()) shown else shown.filter { it.path in selected }, photoMeta, onOpen = { i, l -> open(i, l) },
                            onPick = { l -> placeFilter = l.map { it.path }.toSet(); atlas = false })
                        else if (showTiles) TileGrid(groups, ui.tile, selected, onClick, onSelect = { selected = it }, groupLabel = { groupLabel(ctx, it, area == Area.Photos && !searching, ui.group) },
                            onTile = { ui.useTile(it) }, names = area != Area.Photos || searching, fit = area == Area.Photos && !searching && photoFit, state = gridState)
                        else DetailList(groups, selected, showPath, if (area == Area.Recent && !searching) used else null, onClick, onLong, onMenu, offlineInfo,
                            officeOf = { Office.mode(it, office) }, shownOf = { repo.isShownCached(it.path) },
                            groupLabel = { groupLabel(ctx, it, false, ui.group) })
                        }
                    }
                }
            }
        }
    }

    excludeFrom?.let { p -> ExcludeFolderDialog(p) { changed -> excludeFrom = null; if (changed) { selected = emptySet(); reload() } } }
    shareSheet?.let { items ->
        val own = items.none { it.path.startsWith(FilesRepo.WITH_ME) || FilesRepo.isAlbum(it.path) || it.path.startsWith("ext:") }
        val files = items.none { it.isDir }
        ShareSheet(
            chat = items.any { it.fileId > 0 },
            journal = files && items.any { FileViews.hasThumb(it) && !FilesRepo.isAlbum(it.path) && !it.path.startsWith("ext:") },
            people = own, album = files && items.any { FileViews.hasThumb(it) } && items.none { FilesRepo.isAlbum(it.path) },
            link = own && items.size == 1, external = files && items.none { repo.isShownCached(it.path) },
            onDismiss = { shareSheet = null }) { how ->
            shareSheet = null
            when (how) {
                "chat" -> sendChat = items.filter { it.fileId > 0 }.map { at.dimes.dmoffice.chat.ChatOutRef(if (it.isDir) "folder" else "file", fileId = it.fileId) }
                "journal" -> toJournal(items)
                "people" -> action = FileAction.People(items)
                "album" -> albumAsk = AlbumAsk.Pick(items.map { it.path })
                "link" -> action = FileAction.Link(items[0])
                "external" -> external(items, share = true)
            }
        }
    }
    journalPick?.let { (paths, list) ->
        androidx.compose.material3.AlertDialog(onDismissRequest = { journalPick = null },
            title = { Text(stringResource(R.string.journal_from_files_pick)) },
            text = { Column { for (j in list) Text(j.name + (j.owner?.let { " · $it" } ?: ""), Modifier.fillMaxWidth().clickable {
                journalPick = null
                scope.launch {
                    try {
                        val (entries, photos) = App.get(ctx).journal.fromFiles(j.id, paths)
                        selected = emptySet()
                        android.widget.Toast.makeText(ctx, ctx.resources.getQuantityString(R.plurals.share_done_journal, entries, entries, photos), android.widget.Toast.LENGTH_LONG).show()
                    } catch (e: Exception) { error = e.message }
                }
            }.padding(vertical = 12.dp)) } },
            confirmButton = {}, dismissButton = { TextButton(onClick = { journalPick = null }) { Text(stringResource(android.R.string.cancel)) } })
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
                for (i in FileViews.unstack(a.items)) if (a.copy || dir != FilesRepo.parent(i.path)) repo.transfer(i.path, FilesRepo.join(dir, i.name), a.copy, if (a.copy) "rename" else "fail")
                FilesProvider.notifyChildren(ctx, dir)
            }
        }
        is FileAction.Delete -> ConfirmDialog(stringResource(R.string.delete),
            a.items.singleOrNull()?.let { stringResource(if (it.isDir) R.string.files_delete_folder_confirm else R.string.files_delete_confirm, it.name) }
                ?: stringResource(R.string.files_delete_many_confirm, a.items.size),
            stringResource(R.string.delete), onDismiss = { action = null }) { action = null; delete(FileViews.unstack(a.items)) }
        is FileAction.Link -> LinkDialog(a.item, a.existing, onDismiss = { action = null }, onDone = { action = null; selected = emptySet(); reload() })
        is FileAction.People -> {
            val one = a.items.singleOrNull()
            PeopleShareDialog(
                title = one?.name ?: ctx.getString(R.string.files_selected, a.items.size),
                intro = stringResource(if (one == null) R.string.files_people_intro_many else if (one.isDir) R.string.files_people_intro_dir else R.string.files_people_intro_file),
                // Mehrere: leer anfangen, die Gewählten kommen bei jedem dazu (wer schon Zugriff hat, behält ihn)
                load = { if (one != null) withContext(Dispatchers.IO) { repo.shareLevels(one.path) } else emptyMap() },
                save = { levels -> withContext(Dispatchers.IO) {
                    if (one != null) repo.setShareLevels(one.path, levels)
                    else for (i in a.items) repo.setShareLevels(i.path, repo.shareLevels(i.path) + levels)
                } },
                onDismiss = { action = null }, onSaved = { action = null; selected = emptySet(); reload() }, onError = { error = it })
        }
        is FileAction.Versions -> VersionsDialog(a.item, onDismiss = { action = null }, onChanged = { reload() })
        is FileAction.Details -> DetailsDialog(a.item, onDismiss = { action = null })
        is FileAction.Labels -> LabelsDialog(a.items.map { it.path }, onDismiss = { action = null }, onDone = { reload() }, onError = { error = it })
        is FileAction.Info -> PhotoInfoDialog(a.items, photoMeta, onDismiss = { action = null }, onDone = { reload() }, onError = { error = it })
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
/** Galerie: Gruppen, die der Server bildet (Name im Schlüssel nach SERVER_GROUP) */
private val SERVER_GROUPS = setOf("source", "category", "tag")
private const val SERVER_GROUP = ""

private fun groupLabel(ctx: Context, key: String, month: Boolean, by: String): String {
    if (key.isEmpty()) return ""
    if (key.startsWith(SERVER_GROUP)) return key.drop(1).let { l ->
        when {
            l == "Eigene Dateien" -> ctx.getString(R.string.photos_group_own)
            l == "Ohne Kategorie" -> ctx.getString(R.string.photos_group_no_category)
            l == "Ohne Tag" -> ctx.getString(R.string.photos_group_no_tag)
            l.startsWith("Geteilt von ") -> ctx.getString(R.string.photos_group_shared_by, l.removePrefix("Geteilt von "))
            else -> l
        }
    }
    // Galerie: Jahr ("2026"), Monat ("2026-10") oder Tag ("2026-10-24")
    if (month) return try {
        when (key.length) {
            4 -> key
            10 -> java.time.LocalDate.parse(key).format(java.time.format.DateTimeFormatter.ofLocalizedDate(java.time.format.FormatStyle.FULL))
            else -> { val ym = YearMonth.parse(key); ym.month.getDisplayName(TextStyle.FULL_STANDALONE, java.util.Locale.getDefault()) + " " + ym.year }
        }
    } catch (_: Exception) { key }
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
private fun AreaChips(area: Area, onArea: (Area) -> Unit, onTresor: (() -> Unit)? = null) {
    // Gewaehlten Bereich sichtbar halten (die Leiste ist breiter als das Telefon)
    val state = androidx.compose.foundation.lazy.rememberLazyListState(initialFirstVisibleItemIndex = (area.ordinal - 1).coerceAtLeast(0))
    LaunchedEffect(area) { state.animateScrollToItem((area.ordinal - 1).coerceAtLeast(0)) }
    LazyRow(state = state, contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        items(Area.entries.toList()) { a ->
            FilterChip(selected = a == area, onClick = { onArea(a) }, label = { Text(stringResource(a.label)) },
                leadingIcon = { Icon(a.icon, contentDescription = null, modifier = Modifier.size(18.dp)) })
        }
        if (onTresor != null) item { TresorChip(onTresor) }
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
private fun MoreMenu(area: Area, onNewFolder: (() -> Unit)?, onAutoUpload: (() -> Unit)?, onRefresh: () -> Unit, onEmptyTrash: () -> Unit, onExcludes: (() -> Unit)? = null,
                     onTresorDownload: (() -> Unit)? = null) {
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            if (area == Area.Files && onNewFolder != null) DropdownMenuItem(text = { Text(stringResource(R.string.files_new_folder)) }, leadingIcon = { Icon(Icons.Default.CreateNewFolder, null) }, onClick = { open = false; onNewFolder() })
            if (area == Area.Trash) DropdownMenuItem(text = { Text(stringResource(R.string.files_trash_empty)) }, leadingIcon = { Icon(Icons.Default.DeleteForever, null) }, onClick = { open = false; onEmptyTrash() })
            if (onExcludes != null) DropdownMenuItem(text = { Text(stringResource(R.string.photos_excluded_title)) }, leadingIcon = { Icon(Icons.Default.HideImage, null) }, onClick = { open = false; onExcludes() })
            DropdownMenuItem(text = { Text(stringResource(R.string.refresh)) }, leadingIcon = { Icon(Icons.Default.Refresh, null) }, onClick = { open = false; onRefresh() })
            if (onTresorDownload != null) DropdownMenuItem(text = { Text(stringResource(R.string.tresor_download)) }, leadingIcon = { Icon(Icons.Default.Download, null) }, onClick = { open = false; onTresorDownload() })
            if (onAutoUpload != null) DropdownMenuItem(text = { Text(stringResource(R.string.au_title)) }, leadingIcon = { Icon(Icons.Default.CloudUpload, null) }, onClick = { open = false; onAutoUpload() })
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SelectionBar(sel: List<FileItem>, onClear: () -> Unit, onSelectAll: () -> Unit, onAction: (String) -> Unit, insets: androidx.compose.foundation.layout.WindowInsets?, photos: Boolean = false) {
    var more by remember { mutableStateOf(false) }
    val one = sel.singleOrNull()
    val enc = sel.any { FilesRepo.isTresor(it.path) }
    TopAppBar(windowInsets = insets ?: androidx.compose.material3.TopAppBarDefaults.windowInsets,
        title = { Text(stringResource(R.string.files_selected, sel.size), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleMedium) },
        navigationIcon = { IconButton(onClick = onClear) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } },
        actions = {
            if (!enc) IconButton(onClick = { onAction("sheet") }) { Icon(Icons.Default.Share, contentDescription = stringResource(R.string.files_share)) }
            if (!enc) IconButton(onClick = { onAction("fav") }) { Icon(if (sel.isNotEmpty() && sel.all { it.favorite }) Icons.Default.Star else Icons.Default.StarBorder, contentDescription = stringResource(R.string.files_favorite)) }
            IconButton(onClick = { onAction("move") }) { Icon(Icons.Default.DriveFileMove, contentDescription = stringResource(R.string.files_move)) }
            IconButton(onClick = { onAction("delete") }) { Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.delete)) }
            Box {
                IconButton(onClick = { more = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
                DropdownMenu(expanded = more, onDismissRequest = { more = false }) {
                    DropdownMenuItem(text = { Text(stringResource(R.string.files_copy)) }, leadingIcon = { Icon(Icons.Default.ContentCopy, null) }, onClick = { more = false; onAction("copy") })
                    if (one != null) {
                        DropdownMenuItem(text = { Text(stringResource(R.string.note_rename)) }, onClick = { more = false; onAction("rename") })
                        if (!one.isDir && !enc) DropdownMenuItem(text = { Text(stringResource(R.string.files_versions)) }, onClick = { more = false; onAction("versions") })
                        DropdownMenuItem(text = { Text(stringResource(R.string.files_details)) }, onClick = { more = false; onAction("details") })
                        if (photos && !one.isDir) DropdownMenuItem(text = { Text(stringResource(R.string.photos_exclude)) }, onClick = { more = false; onAction("exclude") })
                    }
                    if (sel.any { !it.isDir } && !enc) DropdownMenuItem(text = { Text(stringResource(R.string.cats_labels)) }, onClick = { more = false; onAction("labels") })
                    if (!enc && photoInfoWritable(sel)) DropdownMenuItem(text = { Text(stringResource(R.string.photo_info_menu)) }, onClick = { more = false; onAction("info") })

                    DropdownMenuItem(text = { Text(stringResource(R.string.files_select_all)) }, onClick = { more = false; onSelectAll() })
                }
            }
        })
}

/** Offener Album-Dialog: Album fuer die Auswahl waehlen, neues anlegen (mit Fotos), umbenennen. */
private sealed class AlbumAsk {
    data class Pick(val paths: List<String>) : AlbumAsk()
    data class New(val paths: List<String>) : AlbumAsk()
    data object Rename : AlbumAsk()
}

/** Leiste ueber der Galerie: „Alle Fotos“ und die Alben (eigene, geteilte, smarte), dazu Neu und das Menue des gewaehlten. */
@Composable
private fun AlbumBar(albums: List<at.dimes.dmoffice.files.AlbumInfo>, current: Long?, onPick: (Long?) -> Unit, onNew: () -> Unit, onRename: () -> Unit, onDelete: () -> Unit,
    filtered: Boolean = false, onOptions: () -> Unit = {}, atlas: Boolean = false, onAtlas: () -> Unit = {}, people: Boolean = false, onPeople: (() -> Unit)? = null) {
    var menu by remember { mutableStateOf(false) }
    val cur = albums.firstOrNull { it.id == current }
    Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        androidx.compose.foundation.lazy.LazyRow(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            item { FilterChip(selected = current == null, onClick = { onPick(null) }, label = { Text(stringResource(R.string.album_all)) }) }
            items(albums.size) { n ->
                val a = albums[n]
                FilterChip(selected = a.id == current, onClick = { onPick(a.id) }, label = { Text(a.name, maxLines = 1) },
                    leadingIcon = { Icon(if (!a.fixed) Icons.Default.AutoAwesome else if (!a.mine || a.shared) Icons.Default.People else Icons.Default.PhotoLibrary, null, Modifier.size(16.dp)) })
            }
            item { AssistChip(onClick = onNew, label = { Text(stringResource(R.string.album_new)) }, leadingIcon = { Icon(Icons.Default.Add, null, Modifier.size(16.dp)) }) }
        }
        // Karte (Atlas) oder Kacheln
        IconButton(onClick = onAtlas) { Icon(if (atlas) Icons.Default.GridView else Icons.Default.Map, contentDescription = stringResource(if (atlas) R.string.photos_tiles else R.string.photos_atlas)) }
        // Personen
        if (onPeople != null) IconButton(onClick = onPeople) {
            Icon(Icons.Default.Face, contentDescription = stringResource(R.string.people_title), tint = if (people) MaterialTheme.colorScheme.primary else LocalContentColor.current)
        }
        // Gruppieren und Filtern
        IconButton(onClick = onOptions) { Icon(Icons.Default.Tune, contentDescription = stringResource(R.string.photos_options), tint = if (filtered) MaterialTheme.colorScheme.primary else LocalContentColor.current) }
        if (cur != null) Box {
            IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                if (cur.mine) DropdownMenuItem(text = { Text(stringResource(R.string.album_rename)) }, onClick = { menu = false; onRename() })
                DropdownMenuItem(text = { Text(stringResource(if (cur.mine) R.string.album_delete else R.string.album_leave)) }, onClick = { menu = false; onDelete() })
            }
        }
    }
}

/** Filter der Galerie wie die Filterleiste im Web: je Eigenschaft gewaehlte Werte, '' = unbekannt. */
private object PhotoFilters {
    val KEYS = listOf("year", "rating", "label", "source", "camera", "lens", "keyword", "title", "description", "type")
    fun values(key: String, item: FileItem, m: at.dimes.dmoffice.files.PhotoMeta?): List<String> = when (key) {
        "year" -> listOf(java.time.Instant.ofEpochMilli(m?.taken ?: item.modifiedMs).atZone(java.time.ZoneId.systemDefault()).year.toString())
        "camera" -> listOf(m?.camera ?: "")
        "lens" -> listOf(m?.lens ?: "")
        "keyword" -> m?.keywords?.split('\n')?.filter { it.isNotBlank() }?.ifEmpty { null } ?: listOf("")
        "title" -> listOf(m?.title?.trim() ?: "")
        "description" -> listOf(m?.description?.trim() ?: "")
        "rating" -> listOf((m?.rating ?: 0).toString())
        "label" -> listOf(m?.label ?: "")
        // Quelle wie auf dem Server: eigene Ablage („own“) oder ein Galerie-Ordner („ext:<Kennung>“)
        "source" -> listOf(item.path.takeIf { it.startsWith("ext:") }?.let { p -> p.indexOf('/', 4).takeIf { it > 4 }?.let { p.substring(0, it) } } ?: "own")
        else -> listOf(item.name.substringAfterLast('.', "").uppercase())
    }
    fun passes(item: FileItem, m: at.dimes.dmoffice.files.PhotoMeta?, filter: Map<String, Set<String>>) =
        filter.all { (k, sel) -> sel.isEmpty() || values(k, item, m).any { it in sel } }
}

/** Galerie: Gruppieren (Jahr, Monat, Tag) und Filtern (Jahr, Bewertung, Farbmarkierung, Quelle, Kamera, Objektiv, Stichwort, Dateityp) mit Anzahl je Wert. */
@OptIn(ExperimentalMaterial3Api::class, androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
private fun PhotoOptionsSheet(items: List<FileItem>, meta: Map<String, at.dimes.dmoffice.files.PhotoMeta>, group: String, filter: Map<String, Set<String>>,
    onGroup: (String) -> Unit, onFilter: (Map<String, Set<String>>) -> Unit, onDismiss: () -> Unit) {
    androidx.compose.material3.ModalBottomSheet(onDismissRequest = onDismiss) {
        val app = at.dimes.dmoffice.App.get(LocalContext.current)
        val sourceNames by androidx.compose.runtime.produceState(emptyMap<String, String>()) { value = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { app.files.gallerySourceNames() } }
        var open by remember { mutableStateOf<String?>(null) }
        var all by remember { mutableStateOf(false) }
        var search by remember { mutableStateOf("") }
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.photos_filter), style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
                if (filter.values.any { it.isNotEmpty() }) TextButton(onClick = { onFilter(emptyMap()) }) { Text(stringResource(R.string.photos_filter_clear)) }
            }
            for (key in PhotoFilters.KEYS) {
                // Werte mit Anzahl, gezaehlt unter den uebrigen Filtern (wie im Web: jede Spalte zaehlt, was die anderen durchlassen)
                val others = filter - key
                val counts = items.filter { PhotoFilters.passes(it, meta[it.path], others) }
                    .flatMap { i -> PhotoFilters.values(key, i, meta[i.path]).distinct() }.groupingBy { it }.eachCount()
                if (counts.size < 2 && filter[key].isNullOrEmpty()) continue
                // Eine Zeile je Eigenschaft: „Kamera: Alle ›“ bzw. die gewaehlten Werte; antippen klappt die Liste auf
                val sel = filter[key] ?: emptySet()
                val unknown = stringResource(R.string.photos_unknown)
                val noTitle = stringResource(R.string.photos_no_title)
                val noDescription = stringResource(R.string.photos_no_description)
                val noRating = stringResource(R.string.photos_no_rating); val noLabel = stringResource(R.string.photos_no_label); val own = stringResource(R.string.photos_own)
                val labelNames = mapOf("red" to R.string.photos_label_red, "yellow" to R.string.photos_label_yellow, "green" to R.string.photos_label_green, "blue" to R.string.photos_label_blue,
                    "purple" to R.string.photos_label_purple, "orange" to R.string.photos_label_orange, "pink" to R.string.photos_label_pink, "gray" to R.string.photos_label_gray).mapValues { stringResource(it.value) }
                // Werte zum Anzeigen: Sterne, Farbnamen, Namen der Galerie-Ordner
                fun show(v: String): String = when (key) {
                    "rating" -> if (v == "0") noRating else "★".repeat(v.toIntOrNull() ?: 0)
                    "label" -> if (v.isEmpty()) noLabel else labelNames[v] ?: v
                    "source" -> if (v == "own") own else sourceNames[v] ?: v.removePrefix("ext:")
                    "title" -> v.ifEmpty { noTitle }
                    "description" -> v.ifEmpty { noDescription }
                    else -> v.ifEmpty { unknown }
                }
                androidx.compose.material3.ListItem(
                    headlineContent = { Text(stringResource(when (key) { "year" -> R.string.photos_f_year; "camera" -> R.string.photos_f_camera; "lens" -> R.string.photos_f_lens
                        "keyword" -> R.string.photos_f_keyword; "rating" -> R.string.photos_f_rating; "label" -> R.string.photos_f_label; "source" -> R.string.photos_f_source
                        "title" -> R.string.photos_f_title; "description" -> R.string.photos_f_description; else -> R.string.photos_f_type })) },
                    supportingContent = { Text(if (sel.isEmpty()) stringResource(R.string.photos_all) else sel.joinToString(", ") { show(it) }, maxLines = 1,
                        color = if (sel.isEmpty()) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.primary) },
                    trailingContent = { Icon(if (open == key) Icons.Default.ExpandLess else Icons.Default.ExpandMore, null) },
                    modifier = Modifier.clickable { open = if (open == key) null else key; all = false; search = "" })
                if (open == key) {
                    // haeufigste zuerst (Jahre neueste zuerst), hoechstens 8 - der Rest auf „Alle anzeigen“; bei vielen Werten ein Suchfeld
                    var sorted = if (key == "year" || key == "rating") counts.entries.sortedByDescending { it.key } else counts.entries.sortedWith(compareBy({ -it.value }, { it.key.lowercase() }))
                    if (counts.size > 12) OutlinedTextField(search, { search = it }, Modifier.fillMaxWidth().padding(horizontal = 8.dp), singleLine = true,
                        placeholder = { Text(stringResource(R.string.photos_filter_search)) })
                    if (search.isNotBlank()) sorted = sorted.filter { show(it.key).contains(search.trim(), ignoreCase = true) }
                    val list = if (all || search.isNotBlank()) sorted else sorted.take(8)
                    for ((v, n) in list) {
                        val on = v in sel
                        Row(Modifier.fillMaxWidth().clickable { onFilter(filter + (key to (if (on) sel - v else sel + v))) }.padding(start = 8.dp, end = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                            androidx.compose.material3.Checkbox(checked = on, onCheckedChange = null)
                            Text(show(v), Modifier.weight(1f).padding(start = 8.dp), maxLines = 1)
                            Text(n.toString(), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                    if (!all && search.isBlank() && sorted.size > 8) TextButton(onClick = { all = true }, modifier = Modifier.padding(start = 8.dp)) { Text(stringResource(R.string.photos_show_all, sorted.size)) }
                }
            }
            Spacer(Modifier.height(24.dp))
        }
    }
}

/** Galerie: Ansicht - Reihenfolge (neueste/aelteste zuerst) und Gruppieren (Jahr, Monat, Tag). Die Kachelgroesse geht mit zwei Fingern. */
@Composable
private fun PhotoViewMenu(oldest: Boolean, group: String, onOldest: (Boolean) -> Unit, onGroup: (String) -> Unit, fit: Boolean, onFit: (Boolean) -> Unit) {
    val ctx = LocalContext.current
    var open by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { open = true }) { Icon(Icons.AutoMirrored.Filled.Sort, contentDescription = stringResource(R.string.files_view)) }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            fun check(on: Boolean) = if (on) "✓  " else "     "
            Text(stringResource(R.string.files_sort), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
            DropdownMenuItem(text = { Text(check(!oldest) + ctx.getString(R.string.photos_newest)) }, onClick = { onOldest(false) })
            DropdownMenuItem(text = { Text(check(oldest) + ctx.getString(R.string.photos_oldest)) }, onClick = { onOldest(true) })
            HorizontalDivider()
            Text(stringResource(R.string.photos_group), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
            for ((k, l) in listOf("day" to R.string.photos_group_day, "month" to R.string.photos_group_month, "year" to R.string.photos_group_year,
                    "source" to R.string.photos_group_source, "category" to R.string.photos_group_category, "tag" to R.string.photos_group_tag))
                DropdownMenuItem(text = { Text(check(group == k) + ctx.getString(l)) }, onClick = { onGroup(k) })
            HorizontalDivider()
            Text(stringResource(R.string.photos_tiles), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
            DropdownMenuItem(text = { Text(check(!fit) + ctx.getString(R.string.photos_fill)) }, onClick = { onFit(false) })
            DropdownMenuItem(text = { Text(check(fit) + ctx.getString(R.string.photos_fit)) }, onClick = { onFit(true) })
            HorizontalDivider()
            Text(stringResource(R.string.photos_pinch_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp).widthIn(max = 220.dp))
        }
    }
}

/** Teilen: die Wege in dmOffice zuerst, dann andere Apps (Android-Teilen). */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ShareSheet(chat: Boolean, journal: Boolean, people: Boolean, album: Boolean, link: Boolean, external: Boolean,
                       onDismiss: () -> Unit, onPick: (String) -> Unit) {
    androidx.compose.material3.ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(Modifier.padding(bottom = 24.dp)) {
            Text(stringResource(R.string.files_share), style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp))
            @Composable fun row(on: Boolean, icon: ImageVector, label: Int, key: String) {
                if (on) ListItem(leadingContent = { Icon(icon, null) }, headlineContent = { Text(stringResource(label)) }, modifier = Modifier.clickable { onPick(key) })
            }
            row(chat, Icons.AutoMirrored.Filled.Chat, R.string.chat_in_chat, "chat")
            row(journal, Icons.Default.Book, R.string.journal_from_files, "journal")
            row(people, Icons.Default.People, R.string.files_people_share, "people")
            row(album, Icons.Default.PhotoLibrary, R.string.album_add_to, "album")
            row(link, Icons.Default.Link, R.string.files_link_new, "link")
            if (external) { HorizontalDivider(Modifier.padding(vertical = 4.dp)); row(true, Icons.Default.Share, R.string.files_share_other, "external") }
        }
    }
}

/** Auswahl in der Galerie: in welches feste Album, oder ein neues. */
@Composable
private fun AlbumPickDialog(albums: List<at.dimes.dmoffice.files.AlbumInfo>, onDismiss: () -> Unit, onNew: () -> Unit, onPick: (Long) -> Unit) {
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.album_add_to)) },
        text = {
            Column {
                for (a in albums) Text(a.name + (if (!a.mine) " · " + (a.ownerName ?: "") else ""), Modifier.fillMaxWidth().clickable { onPick(a.id) }.padding(vertical = 12.dp))
                Text("+ " + stringResource(R.string.album_new), color = MaterialTheme.colorScheme.primary, modifier = Modifier.fillMaxWidth().clickable { onNew() }.padding(vertical = 12.dp))
            }
        },
        confirmButton = {}, dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

/** Auswahl in einem Album: teilen und (eigene bzw. als Besitzer alle) herausnehmen - verschieben, loeschen usw. gibt es hier nicht. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun AlbumSelectionBar(sel: List<FileItem>, canRemove: Boolean, onClear: () -> Unit, onSelectAll: () -> Unit, onShare: () -> Unit, onRemove: () -> Unit,
    insets: androidx.compose.foundation.layout.WindowInsets?) {
    TopAppBar(windowInsets = insets ?: androidx.compose.material3.TopAppBarDefaults.windowInsets,
        title = { Text(stringResource(R.string.files_selected, sel.size), maxLines = 1, style = MaterialTheme.typography.titleMedium) },
        navigationIcon = { IconButton(onClick = onClear) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } },
        actions = {
            IconButton(onClick = onShare) { Icon(Icons.Default.Share, contentDescription = stringResource(R.string.files_share)) }
            if (canRemove) IconButton(onClick = onRemove) { Icon(Icons.Default.RemoveCircleOutline, contentDescription = stringResource(R.string.album_remove)) }
            IconButton(onClick = onSelectAll) { Icon(Icons.Default.SelectAll, contentDescription = stringResource(R.string.files_select_all)) }
        })
}

/**
 * Ordner aus Fotos ausschliessen: der Ordner des Bildes oder einer darueber (z. B. bei Android/data/music/.../cover.jpg
 * gleich ganz music); darunter die schon ausgeschlossenen zum Wiederaufnehmen.
 */
@Composable
private fun ExcludeFolderDialog(path: String, onDone: (Boolean) -> Unit) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).files
    val scope = rememberCoroutineScope()
    val parts = path.split('/').dropLast(1)
    val chain = parts.indices.map { parts.subList(0, it + 1).joinToString("/") }
    var pick by remember { mutableStateOf(chain.lastOrNull() ?: "") }
    var existing by remember { mutableStateOf<List<String>>(emptyList()) }
    var changed by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) { existing = try { withContext(Dispatchers.IO) { repo.photoExcludes() } } catch (_: Exception) { emptyList() } }
    fun store(list: List<String>, close: Boolean) = scope.launch {
        try { withContext(Dispatchers.IO) { repo.setPhotoExcludes(list) }; existing = list; changed = true; if (close) onDone(true) }
        catch (e: Exception) { error = e.message ?: e.javaClass.simpleName }
    }
    AlertDialog(
        onDismissRequest = { onDone(changed) },
        title = { Text(stringResource(if (path.isEmpty()) R.string.photos_excluded_title else R.string.photos_exclude_title)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                if (path.isEmpty()) Text(stringResource(if (existing.isEmpty()) R.string.photos_excluded_none else R.string.photos_excluded_manage), style = MaterialTheme.typography.bodySmall)
                else if (chain.isEmpty()) Text(stringResource(R.string.photos_exclude_root))
                else {
                    Text(stringResource(R.string.photos_exclude_hint), style = MaterialTheme.typography.bodySmall)
                    chain.forEachIndexed { i, c ->
                        Row(Modifier.fillMaxWidth().clickable { pick = c }.padding(start = (i * 12).dp), verticalAlignment = Alignment.CenterVertically) {
                            RadioButton(selected = pick == c, onClick = { pick = c })
                            Text(c.substringAfterLast('/'), maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    }
                    Text("/$pick", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                if (existing.isNotEmpty()) {
                    HorizontalDivider(Modifier.padding(vertical = 8.dp))
                    Text(stringResource(R.string.photos_excluded), style = MaterialTheme.typography.labelLarge)
                    for (e in existing) Row(verticalAlignment = Alignment.CenterVertically) {
                        Text("/$e", modifier = Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
                        IconButton(onClick = { store(existing - e, false) }) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.photos_include_again)) }
                    }
                }
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = {
            if (path.isEmpty()) TextButton(onClick = { onDone(changed) }) { Text(stringResource(R.string.close)) }
            else TextButton(onClick = { store(existing + pick, true) }, enabled = pick.isNotEmpty() && pick !in existing) { Text(stringResource(R.string.photos_exclude_do)) }
        },
        dismissButton = { if (path.isNotEmpty()) TextButton(onClick = { onDone(changed) }) { Text(stringResource(R.string.cancel)) } },
    )
}

/** Vorschaubild vom Server (zwischengespeichert), sonst das Symbol der Art. */
@Composable
internal fun ThumbOrIcon(item: FileItem, modifier: Modifier, iconSize: androidx.compose.ui.unit.Dp, offlineRoot: Boolean = false, fit: Boolean = false) {
    val ctx = LocalContext.current
    val bmp by produceState(if (FileViews.hasThumb(item)) Thumbs.cached(item, Thumbs.SMALL) else null, item.path, item.etag) {
        if (value == null && FileViews.hasThumb(item)) {
            // Wie Immich: beim schnellen Durchscrollen nur Platzhalter - vom Server erst, wenn die Kachel kurz stehen bleibt
            // (fliegt sie vorher aus dem Bild, bricht das Warten ab und es wird gar nicht geladen)
            if (!withContext(Dispatchers.IO) { Thumbs.onDisk(ctx, item, Thumbs.SMALL) }) kotlinx.coroutines.delay(180)
            value = try { Thumbs.load(ctx, item, Thumbs.SMALL) } catch (_: Exception) { null }
        }
    }
    Box(modifier, contentAlignment = Alignment.Center) {
        val b = bmp
        if (b != null) Image(b.asImageBitmap(), contentDescription = null, contentScale = if (fit) ContentScale.Fit else ContentScale.Crop, modifier = Modifier.fillMaxSize())
        // Bild, das noch kommt: nur die Flaeche als Platzhalter (kein Symbol, das beim Scrollen flackert)
        else if (FileViews.hasThumb(item) && !item.isDir) Unit
        else Icon(if (offlineRoot) Icons.Default.OfflinePin else iconFor(item), contentDescription = null, modifier = Modifier.size(iconSize),
            tint = if (item.isDir) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun DetailList(groups: List<FileViews.Group>, selected: Set<String>, showPath: Boolean, used: Map<String, Long>?,
                       onClick: (FileItem) -> Unit, onLong: (FileItem) -> Unit, onMenu: (FileItem, String) -> Unit,
                       offlineInfo: (FileItem) -> Pair<Boolean, Boolean>, officeOf: (FileItem) -> String?, groupLabel: (String) -> String,
                       shownOf: (FileItem) -> Boolean = { false }) {
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
                        else ItemMenu(item, isRoot, canOffline, officeOf(item), shownOf(item)) { onMenu(item, it) }
                    })
            }
        }
    }
}

@Composable
/** shown: nur gezeigt (Version 2) - ohne Weitergeben, Speichern und Kopieren. */
private fun ItemMenu(item: FileItem, offlineRoot: Boolean, canMarkOffline: Boolean, officeMode: String?, shown: Boolean, onAction: (String) -> Unit) {
    var menu by remember { mutableStateOf(false) }
    Box {
        IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
            @Composable fun entry(label: Int, key: String, color: Color = Color.Unspecified) =
                DropdownMenuItem(text = { Text(stringResource(label), color = color) }, onClick = { menu = false; onAction(key) })
            if (!item.isDir) {
                entry(R.string.files_open, "open")
                if (officeMode != null) entry(if (officeMode == Office.EDIT) R.string.files_office_edit else R.string.files_office_view, "office")
                if (!shown) {
                    entry(R.string.files_open_with, "openwith")
                    entry(R.string.files_save_to_device, "save")
                }
            }
            // Im Tresor: kein Teilen, keine Favoriten, Kategorien oder Versionen
            val enc = FilesRepo.isTresor(item.path)
            if (!enc) entry(R.string.files_share_dots, "share")
            if (!enc) entry(if (item.favorite) R.string.files_favorite_remove else R.string.files_favorite_add, "fav")
            // Nur eigene Dateien (nicht „Mit mir geteilt“, Alben, externe Ordner)
            if (!item.isDir && !shown && !enc && !FilesRepo.isAlbum(item.path) && !item.path.startsWith("ext:") && !item.path.startsWith(FilesRepo.WITH_ME)) entry(R.string.cats_labels, "labels")
            if (offlineRoot) entry(R.string.files_offline_remove, "offline") else if (canMarkOffline) entry(R.string.files_offline_make, "offline")
            entry(R.string.note_rename, "rename")
            entry(R.string.files_move, "move")
            if (!shown) entry(R.string.files_copy, "copy")
            if (!item.isDir && !enc) entry(R.string.files_versions, "versions")
            entry(R.string.files_details, "details")
            entry(R.string.files_select, "select")
            entry(R.string.delete, "delete", MaterialTheme.colorScheme.error)
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun TileGrid(groups: List<FileViews.Group>, tile: String, selected: Set<String>, onClick: (FileItem) -> Unit, onSelect: (Set<String>) -> Unit, groupLabel: (String) -> String,
    onTile: (String) -> Unit = {}, names: Boolean = true, fit: Boolean = false,
    state: androidx.compose.foundation.lazy.grid.LazyGridState = androidx.compose.foundation.lazy.grid.rememberLazyGridState()) {
    val tileNow by androidx.compose.runtime.rememberUpdatedState(tile)
    val min = when (tile) { "small" -> 88.dp; "large" -> 180.dp; else -> 120.dp }
    // Markieren wie in Galerie-Apps: lange druecken markiert (auf Markiertem: hebt auf), weiterwischen nimmt alles
    // dazwischen mit; am oberen oder unteren Rand laeuft die Ansicht mit
    val haptic = androidx.compose.ui.platform.LocalHapticFeedback.current
    val density = androidx.compose.ui.platform.LocalDensity.current
    val flat = remember(groups) { groups.flatMap { g -> g.items.map { it.path } } }
    val sel by androidx.compose.runtime.rememberUpdatedState(selected)
    val select by androidx.compose.runtime.rememberUpdatedState(onSelect)
    var base by remember { mutableStateOf<Set<String>?>(null) }
    var anchor by remember { mutableStateOf(-1) }
    var adding by remember { mutableStateOf(true) }
    var finger by remember { mutableStateOf(androidx.compose.ui.geometry.Offset.Unspecified) }
    fun hit(o: androidx.compose.ui.geometry.Offset): Int? = state.layoutInfo.visibleItemsInfo.firstOrNull {
        val k = it.key as? String
        k != null && !k.startsWith("g:") && o.x >= it.offset.x && o.x < it.offset.x + it.size.width && o.y >= it.offset.y && o.y < it.offset.y + it.size.height
    }?.let { flat.indexOf(it.key as String) }?.takeIf { it >= 0 }
    fun spanTo(i: Int) {
        val b = base ?: return
        val range = (minOf(anchor, i)..maxOf(anchor, i)).map { flat[it] }
        select(if (adding) b + range else b - range.toSet())
    }
    androidx.compose.runtime.LaunchedEffect(base != null) {
        val edge = with(density) { 64.dp.toPx() }
        while (base != null) {
            val h = state.layoutInfo.viewportSize.height
            val speed = when { !finger.isValid() -> 0f; finger.y < edge -> -24f; finger.y > h - edge -> 24f; else -> 0f }
            if (speed != 0f) { state.scrollBy(speed); hit(finger)?.let { spanTo(it) } }
            kotlinx.coroutines.delay(16)
        }
    }
    // Zwei Finger: auseinander groessere, zusammen kleinere Kacheln (klein, mittel, gross)
    Box(Modifier.fillMaxSize().pointerInput(Unit) {
        awaitEachGesture {
            awaitFirstDown(requireUnconsumed = false, pass = androidx.compose.ui.input.pointer.PointerEventPass.Initial)
            var zoom = 1f
            while (true) {
                val e = awaitPointerEvent(androidx.compose.ui.input.pointer.PointerEventPass.Initial)
                if (e.changes.none { it.pressed }) break
                if (e.changes.count { it.pressed } >= 2) {
                    zoom *= e.calculateZoom()
                    e.changes.forEach { it.consume() }
                    val sizes = listOf("small", "medium", "large")
                    val at = sizes.indexOf(tileNow).coerceAtLeast(0)
                    if (zoom > 1.3f && at < 2) { onTile(sizes[at + 1]); zoom = 1f }
                    else if (zoom < 0.77f && at > 0) { onTile(sizes[at - 1]); zoom = 1f }
                }
            }
        }
    }) {
    // Die Geste schon im Durchlauf vor der Liste ansehen: sonst nimmt das Scrollen der Liste die Bewegung, bevor die
    // Auswahl sie bekommt. Bewegt sich der Finger vor Ablauf der Haltezeit, bleibt es beim Scrollen.
    LazyVerticalGrid(GridCells.Adaptive(min), Modifier.fillMaxSize().pointerInput(flat) {
        val initial = androidx.compose.ui.input.pointer.PointerEventPass.Initial
        awaitEachGesture {
            val down = awaitFirstDown(requireUnconsumed = false, pass = initial)
            val held = withTimeoutOrNull(viewConfiguration.longPressTimeoutMillis) {
                while (true) {
                    val e = awaitPointerEvent(initial)
                    val c = e.changes.firstOrNull { it.id == down.id } ?: return@withTimeoutOrNull false
                    if (!c.pressed || (c.position - down.position).getDistance() > viewConfiguration.touchSlop) return@withTimeoutOrNull false
                }
                @Suppress("UNREACHABLE_CODE") false
            } == null
            if (!held) return@awaitEachGesture
            val i = hit(down.position) ?: return@awaitEachGesture
            anchor = i; adding = flat[i] !in sel; base = sel; finger = down.position
            haptic.performHapticFeedback(androidx.compose.ui.hapticfeedback.HapticFeedbackType.LongPress)
            spanTo(i)
            try {
                while (true) {
                    val e = awaitPointerEvent(initial)
                    val c = e.changes.firstOrNull { it.id == down.id } ?: break
                    // Alles verbrauchen: weder Scrollen noch Antippen der Kachel
                    e.changes.forEach { it.consume() }
                    if (!c.pressed) break
                    finger = c.position
                    hit(finger)?.let { spanTo(it) }
                }
            } finally { base = null; finger = androidx.compose.ui.geometry.Offset.Unspecified }
        }
    }, state = state, contentPadding = PaddingValues(start = 8.dp, end = 8.dp, bottom = 88.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        var lastMonth: String? = null
        for (g in groups) {
            // Nach Tagen gruppiert: beim Monatswechsel eine deutliche Monatsueberschrift (wie Immich)
            if (g.key.length == 10 && g.key.take(7) != lastMonth) {
                val ym = g.key.take(7)
                lastMonth = ym
                item(key = "m:" + ym, span = { GridItemSpan(maxLineSpan) }) {
                    val label = try { val y = java.time.YearMonth.parse(ym); y.month.getDisplayName(java.time.format.TextStyle.FULL_STANDALONE, java.util.Locale.getDefault()) + " " + y.year } catch (_: Exception) { ym }
                    Text(label, style = MaterialTheme.typography.headlineSmall, fontWeight = androidx.compose.ui.text.font.FontWeight.SemiBold,
                        modifier = Modifier.fillMaxWidth().padding(start = 8.dp, top = 24.dp, bottom = 4.dp))
                }
            }
            // Kopf mit Haken: ganzen Abschnitt markieren bzw. die Markierung aufheben
            if (g.key.isNotEmpty()) item(key = "g:" + g.key, span = { GridItemSpan(maxLineSpan) }) {
                val paths = g.items.map { it.path }
                val all = paths.all { it in selected }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.weight(1f)) { SectionHeader(groupLabel(g.key)) }
                    IconButton(onClick = { onSelect(if (all) selected - paths.toSet() else selected + paths) }) {
                        Icon(if (all) Icons.Default.CheckCircle else Icons.Outlined.CheckCircle, contentDescription = stringResource(R.string.files_select_section),
                            tint = if (all) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
            items(g.items.size, key = { g.items[it].path }) { n ->
                val item = g.items[n]
                val sel = item.path in selected
                Column(Modifier.clip(RoundedCornerShape(8.dp)).clickable { onClick(item) }
                    .then(if (sel) Modifier.border(3.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(8.dp)) else Modifier)) {
                    Box {
                        ThumbOrIcon(item, Modifier.fillMaxWidth().aspectRatio(1f).background(MaterialTheme.colorScheme.surfaceVariant), if (tile == "small") 32.dp else 48.dp, fit = fit)
                        if (sel) Icon(Icons.Default.CheckCircle, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.align(Alignment.TopEnd).padding(4.dp))
                        if (item.favorite) Icon(Icons.Default.Star, contentDescription = null, tint = Color(0xFFF2B600), modifier = Modifier.align(Alignment.TopStart).padding(4.dp).size(16.dp))
                        // Stapel JPG+RAW: Kennzeichen unten rechts
                        if (item.stack.isNotEmpty()) Text(item.stack.map { it.name.substringAfterLast('.').uppercase() }.distinct().joinToString("+"),
                            style = MaterialTheme.typography.labelSmall, color = Color.White,
                            modifier = Modifier.align(Alignment.BottomEnd).padding(4.dp).background(Color.Black.copy(alpha = 0.6f), RoundedCornerShape(4.dp)).padding(horizontal = 4.dp, vertical = 1.dp))
                    }
                    // Galerie: nur Bilder, keine Bildunterschrift
                    if ((names || !FileViews.hasThumb(item)) && (tile != "small" || !FileViews.hasThumb(item)))
                        Text(item.name, style = MaterialTheme.typography.bodySmall, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(4.dp))
                }
            }
        }
    }
    FastScroller(state, groups, groupLabel, Modifier.align(Alignment.TopEnd))
    }
}

/**
 * Schnellscroll wie bei Immich: beim Scrollen erscheint rechts ein Griff; ihn ziehen springt durch die Gruppen,
 * dabei stehen die Jahre am Rand und eine Blase zeigt den Monat. Nur bei vielen Gruppen.
 */
@Composable
private fun FastScroller(state: androidx.compose.foundation.lazy.grid.LazyGridState, groups: List<FileViews.Group>, label: (String) -> String, modifier: Modifier) {
    // Positionen wie im Raster gezählt, also mit den Monatsüberschriften (nach Tagen gruppiert); sonst zeigte der Griff
    // weiter unten im Zeitstrahl immer weiter neben die tatsächliche Stelle
    val starts = remember(groups) {
        var i = 0
        var lastMonth: String? = null
        groups.map { g ->
            val s = i
            if (g.key.length == 10 && g.key.take(7) != lastMonth) { lastMonth = g.key.take(7); i++ }
            i += (if (g.key.isNotEmpty()) 1 else 0) + g.items.size
            s
        }
    }
    val total = remember(groups) {
        val months = groups.filter { it.key.length == 10 }.map { it.key.take(7) }.zipWithNext().count { (a, b) -> a != b } + (if (groups.any { it.key.length == 10 }) 1 else 0)
        months + groups.sumOf { (if (it.key.isNotEmpty()) 1 else 0) + it.items.size }
    }
    if (groups.size < 2 || total < 24) return
    val scope = rememberCoroutineScope()
    val density = androidx.compose.ui.platform.LocalDensity.current
    var height by remember { mutableStateOf(1) }
    var dragY by remember { mutableStateOf<Float?>(null) }
    var visible by remember { mutableStateOf(false) }
    val scrolling = state.isScrollInProgress
    // Wie Immich: erst nichts; beim Scrollen kommt der Griff (und geht 1,5 s danach wieder), solange man ihn hält
    // die Jahre am Rand und die Monatsblase
    LaunchedEffect(scrolling, dragY != null) {
        if (scrolling || dragY != null) visible = true
        else { kotlinx.coroutines.delay(1500); visible = false }
    }
    val handleH = with(density) { 48.dp.toPx() }
    val track = (height - handleH).coerceAtLeast(1f)
    val frac = dragY?.let { (it / track).coerceIn(0f, 1f) } ?: (state.firstVisibleItemIndex.toFloat() / (total - 1).coerceAtLeast(1))
    fun groupAt(f: Float): Int { val idx = (f * (total - 1)).toInt(); return starts.indexOfLast { it <= idx }.coerceAtLeast(0) }
    // Die Geste lebt über viele Neuaufbauten: Sie muss den aktuellen Stand lesen, nicht den vom Anlegen (sonst springt
    // der Griff beim Antippen an eine alte Stelle)
    val fracNow by androidx.compose.runtime.rememberUpdatedState(frac)
    val trackNow by androidx.compose.runtime.rememberUpdatedState(track)
    // Jahre am Rand: erste Gruppe jedes Jahres (Schlüssel "2026-10")
    val years = remember(groups) { groups.mapIndexedNotNull { n, g -> g.key.take(4).takeIf { it.length == 4 && it.all(Char::isDigit) }?.let { n to it } }.distinctBy { it.second } }
    // Ueber die ganze Flaeche (nur der Griff nimmt Beruehrungen an) - in 1 dp Breite wurde der Griff auf 1 dp gequetscht und war unsichtbar
    Box(modifier.fillMaxSize().onSizeChanged { height = it.height }) {
        if (!visible) return@Box
        if (dragY != null && years.size > 1) for ((n, y) in years) {
            val top = (starts[n].toFloat() / total * track).toInt()
            Text(y, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.align(Alignment.TopEnd).offset { androidx.compose.ui.unit.IntOffset(-with(density) { 52.dp.roundToPx() }, top) }
                    .background(MaterialTheme.colorScheme.surface.copy(alpha = 0.85f), RoundedCornerShape(10.dp)).padding(horizontal = 8.dp, vertical = 2.dp))
        }
        val y = (frac * track).toInt()
        dragY?.let {
            Text(label(groups[groupAt(frac)].key), style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onPrimaryContainer,
                modifier = Modifier.align(Alignment.TopEnd).offset { androidx.compose.ui.unit.IntOffset(-with(density) { 120.dp.roundToPx() }, y) }
                    .background(MaterialTheme.colorScheme.primaryContainer, RoundedCornerShape(20.dp)).padding(horizontal = 14.dp, vertical = 8.dp))
        }
        Box(Modifier.align(Alignment.TopEnd).offset { androidx.compose.ui.unit.IntOffset(0, y) }.size(width = 36.dp, height = 48.dp)
            .background(MaterialTheme.colorScheme.primaryContainer, RoundedCornerShape(topStart = 24.dp, bottomStart = 24.dp))
            .pointerInput(total) {
                detectVerticalDragGestures(
                    onDragStart = { dragY = fracNow * trackNow },
                    onDragEnd = { dragY = null }, onDragCancel = { dragY = null }) { change, amount ->
                    change.consume()
                    val ny = ((dragY ?: 0f) + amount).coerceIn(0f, trackNow)
                    dragY = ny
                    val target = ((ny / trackNow) * (total - 1)).toInt()
                    scope.launch { state.scrollToItem(target) }
                }
            }, contentAlignment = Alignment.Center) {
            Icon(Icons.Default.UnfoldMore, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimaryContainer)
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
    val tresor = FilesRepo.isTresor(path)
    val rel = if (tresor) path.removePrefix(FilesRepo.TRESOR).removePrefix("/") else path
    val parts = if (rel.isEmpty()) emptyList() else rel.split('/')
    val scroll = rememberScrollState()
    LaunchedEffect(path) { scroll.animateScrollTo(scroll.maxValue) }
    Row(Modifier.fillMaxWidth().horizontalScroll(scroll).padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        if (path.isNotEmpty()) IconButton(onClick = { onGo(FilesRepo.parent(path)) }) { Icon(Icons.Default.ArrowUpward, contentDescription = stringResource(R.string.files_up)) }
        if (tresor) TextButton(onClick = { onGo(FilesRepo.TRESOR) }, enabled = path != FilesRepo.TRESOR) {
            Icon(Icons.Outlined.LockOpen, contentDescription = null, tint = TresorOpenColor, modifier = Modifier.size(18.dp).padding(end = 4.dp))
            Text(stringResource(R.string.tresor))
        }
        else TextButton(onClick = { onGo("") }) { Text(stringResource(R.string.files_area_files)) }
        parts.forEachIndexed { i, name ->
            Icon(Icons.Default.ChevronRight, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
            val target = (if (tresor) FilesRepo.TRESOR + "/" else "") + parts.take(i + 1).joinToString("/")
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
    val tresorOn = at.dimes.dmoffice.data.Features.on("vault")
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
                        list != null && list.isEmpty() && !(dir.isEmpty() && tresorOn) -> Column(Modifier.fillMaxWidth().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                            Icon(Icons.Outlined.Folder, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(48.dp))
                            Text(stringResource(R.string.files_no_subfolders), color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 8.dp))
                            TextButton(onClick = { newFolder = true }) { Text(stringResource(R.string.files_new_folder)) }
                        }
                        list != null -> LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 96.dp)) {
                            // Oben in „Meine Dateien“: der Tresor als Ziel (geschlossen fragt der Dialog)
                            if (dir.isEmpty() && tresorOn) item(key = FilesRepo.TRESOR) {
                                ListItem(headlineContent = { Text(stringResource(R.string.tresor)) },
                                    leadingContent = { Icon(Icons.Outlined.Lock, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                                    trailingContent = { Icon(Icons.Default.ChevronRight, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant) },
                                    modifier = Modifier.clickable { dir = FilesRepo.TRESOR })
                            }
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

/**
 * „Tresor herunterladen“ (docs/tresor-cryptomator-konzept.md, 9.1): der System-Tresor verschlüsselt im Cryptomator-Format
 * als Ordner auf dem Gerät. Größe zeigen, Tresor-Passwort, Zielordner wählen; über der Grenze der Hinweis auf die Datensicherung.
 */
@Composable
private fun TresorDownloadDialog(onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var info by remember { mutableStateOf<TresorApi.DownloadInfo?>(null) }
    var password by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var progress by remember { mutableStateOf<Long?>(null) }
    var done by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) { info = try { withContext(Dispatchers.IO) { TresorApi.downloadInfo(ctx) } } catch (e: Exception) { error = e.message; null } }
    val tooBig = info?.let { it.limit > 0 && it.size > it.limit } == true
    val tree = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        val pw = password
        progress = 0
        error = null
        scope.launch {
            try { done = withContext(Dispatchers.IO) { TresorApi.downloadTo(ctx, pw, uri) { n -> progress = n } } }
            catch (e: Exception) { error = e.message ?: e.toString() }
            finally { progress = null; password = "" }
        }
    }
    AlertDialog(onDismissRequest = { if (progress == null) onDismiss() },
        title = { Text(stringResource(R.string.tresor_download)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(stringResource(R.string.tresor_download_hint))
                info?.let { Text(stringResource(R.string.tresor_download_size, sizeText(it.size)), style = MaterialTheme.typography.bodySmall) }
                if (info?.available == false) Text(stringResource(R.string.tresor_download_not_ready))
                if (tooBig) Text(stringResource(R.string.tresor_download_too_big, sizeText(info!!.limit)))
                if (done != null) Text(stringResource(R.string.tresor_download_done, done!!), color = MaterialTheme.colorScheme.primary)
                else if (info?.available == true && !tooBig) OutlinedTextField(password, { password = it }, label = { Text(stringResource(R.string.tresor_password)) }, singleLine = true,
                    visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation(), enabled = progress == null, modifier = Modifier.fillMaxWidth())
                progress?.let { Text(stringResource(R.string.tresor_download_progress, sizeText(it)), style = MaterialTheme.typography.bodySmall); LinearProgressIndicator(Modifier.fillMaxWidth()) }
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = {
            if (done != null) TextButton(onClick = onDismiss) { Text(stringResource(R.string.close)) }
            else TextButton(onClick = { tree.launch(null) }, enabled = info?.available == true && !tooBig && password.isNotEmpty() && progress == null) { Text(stringResource(R.string.tresor_download_pick)) }
        },
        dismissButton = { if (done == null) TextButton(onClick = onDismiss, enabled = progress == null) { Text(stringResource(R.string.cancel)) } })
}

/** Tresor ohne Netz: die Offline-Kopie mit dem Tresor-Passwort öffnen (masterkey.cryptomator, auf dem Gerät). */
@Composable
private fun TresorOfflineUnlock(onDismiss: () -> Unit, onOpen: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var password by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    val wrong = stringResource(R.string.tresor_offline_wrong)
    AlertDialog(onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(stringResource(R.string.tresor_offline_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(stringResource(R.string.tresor_offline_hint))
                OutlinedTextField(password, { password = it }, label = { Text(stringResource(R.string.tresor_password)) }, singleLine = true, enabled = !busy,
                    visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation(), modifier = Modifier.fillMaxWidth())
                if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = {
            TextButton(enabled = password.isNotEmpty() && !busy, onClick = {
                busy = true; error = null
                scope.launch {
                    try { withContext(Dispatchers.IO) { at.dimes.dmoffice.files.TresorOffline.unlock(ctx, password) }; onOpen() }
                    catch (_: Exception) { error = wrong }
                    finally { busy = false }
                }
            }) { Text(stringResource(R.string.tresor_open_button)) }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !busy) { Text(stringResource(R.string.cancel)) } })
}
