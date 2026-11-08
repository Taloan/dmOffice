package at.dimes.dmoffice.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.FolderShared
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.DocumentScanner
import androidx.compose.material.icons.filled.Archive
import androidx.compose.material.icons.filled.People
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.AssistChip
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.ScaffoldDefaults
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.NoteItem
import at.dimes.dmoffice.data.NotesRepo
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.TextButton
import androidx.compose.material.icons.outlined.LockOpen
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable

/** notizi-Datenbanken im Tresor (GET /api/notes/databases, Name nur bei offenem Tresor). */
@Serializable private data class VaultDb(val folder: String, val name: String? = null)
@Serializable private data class VaultDbs(val tresor: List<VaultDb> = emptyList())

/**
 * Notizen wie in notizi: Ordner oben, darunter die Notizen des Ordners (wichtige zuerst).
 * Filter Wichtig und Anhang zeigen passende Notizen aus allen Ordnern; die Suche geht ueber den
 * ganzen Text (Server), ohne Verbindung ueber Titel und Vorschau im Cache. Archiv und Papierkorb
 * kommen jedes Mal vom Server.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NotesScreen(onOpen: (Long) -> Unit, onClose: (() -> Unit)?) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    // Suche (Version 2): Lupe in der Kopfzeile statt eines Suchfelds
    val search = rememberTabSearch()
    val q = if (search.open) search.query else ""
    var folder by rememberSaveable { mutableStateOf<String?>(null) }
    var view by rememberSaveable { mutableStateOf<String?>(null) }          // null, "archive", "trash", "shared" (Version 2)
    var onlyImportant by rememberSaveable { mutableStateOf(false) }
    var onlyAttachments by rememberSaveable { mutableStateOf(false) }
    // Gezeigte Datenbank: die gewählte oder eine im Tresor (bleibt beim Öffnen einer Notiz erhalten, App.vaultNotes)
    var repo by remember { mutableStateOf(app.shownNotes) }
    val inVault = repo.vault != null
    var vaultTitle by rememberSaveable { mutableStateOf<String?>(null) }
    var vaultChoice by remember { mutableStateOf<List<VaultDb>?>(null) }
    val vaultOn = at.dimes.dmoffice.data.Features.on("vault")
    val all by remember(repo) { repo.all() }.collectAsState(initial = emptyList())
    val folders by remember(repo) { repo.folders() }.collectAsState(initial = emptyList())
    var error by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(false) }
    var creating by remember { mutableStateOf(false) }
    var recipeScan by rememberSaveable { mutableStateOf(false) }
    var found by remember { mutableStateOf<List<NoteItem>?>(null) }      // Treffer vom Server
    var viewItems by remember { mutableStateOf<List<NoteItem>?>(null) }  // Archiv, Papierkorb
    var owners by remember { mutableStateOf<Map<Long, String>>(emptyMap()) }   // einzeln mit mir geteilt: Besitzer
    // Mit mir geteilte Ordner: die Freigaben und der gerade offene Ordner darin (null = Übersicht „Mit mir geteilt“)
    var sharedRoots by remember { mutableStateOf<List<NoteShares.SharedRoot>>(emptyList()) }
    var sharedFolder by rememberSaveable { mutableStateOf<String?>(null) }
    var sharedItems by remember { mutableStateOf<List<NoteItem>?>(null) }
    val sharedRoot = sharedFolder?.let { id -> sharedRoots.firstOrNull { r -> r.folders.any { it.syncId == id } } }

    suspend fun refresh() {
        loading = true
        try { repo.refresh(); error = null } catch (e: Exception) { error = e.message } finally { loading = false }
    }
    LaunchedEffect(repo) { refresh() }

    // ---------------- notizi-Datenbanken im Tresor ----------------
    fun enterVault(d: VaultDb) {
        app.vaultNotes?.forget()
        val r = NotesRepo(ctx, app.db, d.folder)
        app.vaultNotes = r
        vaultTitle = d.name ?: d.folder
        folder = null; view = null; onlyImportant = false; onlyAttachments = false
        repo = r
    }
    fun leaveVault() {
        app.vaultNotes?.forget()
        app.vaultNotes = null
        folder = null; view = null
        repo = app.notes
    }
    suspend fun pickVault() {
        val st = try { withContext(Dispatchers.IO) { TresorApi.state(ctx) } } catch (e: Exception) { error = e.message; return }
        if (!st.exists) { error = ctx.getString(R.string.tresor_setup_in_settings); return }
        if (!st.open && !requestTresorOpen()) return
        val list = try {
            withContext(Dispatchers.IO) { val a = app.api ?: return@withContext emptyList<VaultDb>(); a.get<VaultDbs>(a.url("/api/notes/databases")).tresor }
        } catch (e: Exception) { error = e.message; return }
        when (list.size) {
            0 -> error = ctx.getString(R.string.notes_tresor_empty)
            1 -> enterVault(list[0])
            else -> vaultChoice = list
        }
    }
    // Tresor zu (hier, im Web oder abgelaufen): sofort zurück, nichts bleibt im Speicher
    val lockChanged by TresorGate.changed.collectAsState()
    LaunchedEffect(lockChanged, inVault) {
        if (!inVault) return@LaunchedEffect
        val st = try { withContext(Dispatchers.IO) { TresorApi.state(ctx) } } catch (_: Exception) { null }
        if (st != null && !st.open) leaveVault()
    }
    // Suche: kurz warten, dann im ganzen Text auf dem Server; ohne Verbindung im Cache
    LaunchedEffect(q) {
        found = null
        if (q.isBlank()) return@LaunchedEffect
        delay(300)
        found = try { repo.searchServer(q) } catch (_: Exception) { null }
    }
    suspend fun loadView(v: String) {
        viewItems = try {
            if (v == "shared") {
                sharedRoots = try { NoteShares.folders(ctx) } catch (_: Exception) { emptyList() }
                NoteShares.withMe(ctx).also { l -> owners = l.associate { it.id to it.owner } }.map { it.asItem() }
            } else repo.view(v)
        } catch (e: Exception) { error = e.message; emptyList() }
    }
    LaunchedEffect(view) {
        viewItems = null
        if (view != "shared") sharedFolder = null
        loadView(view ?: return@LaunchedEffect)
    }
    // Notizen des offenen geteilten Ordners - vom Server
    LaunchedEffect(sharedFolder) {
        sharedItems = null
        val f = sharedFolder ?: return@LaunchedEffect
        sharedItems = try { repo.folderItems(f) } catch (e: Exception) { error = e.message; emptyList() }
    }

    fun up() {
        when {
            sharedFolder != null -> sharedFolder = sharedRoot?.folders?.firstOrNull { it.syncId == sharedFolder }?.parent
            view != null -> view = null
            folder == null && inVault -> leaveVault()
            else -> folder = folders.firstOrNull { it.syncId == folder }?.parent
        }
    }
    BackHandler(enabled = folder != null || view != null || inVault) { up() }

    val filtering = onlyImportant || onlyAttachments
    val title = when {
        view == "archive" -> stringResource(R.string.folder_archive)
        view == "trash" -> stringResource(R.string.folder_trash)
        view == "shared" && sharedFolder != null -> sharedRoot?.folders?.firstOrNull { it.syncId == sharedFolder }?.name ?: stringResource(R.string.notes_shared_with_me)
        view == "shared" -> stringResource(R.string.notes_shared_with_me)
        folder != null -> folders.firstOrNull { it.syncId == folder }?.name ?: stringResource(R.string.notes_tab)
        inVault -> stringResource(R.string.notes_in_tresor, vaultTitle ?: repo.vault ?: "")
        else -> stringResource(R.string.notes_tab)
    }
    fun matches(n: NoteItem) = (!onlyImportant || n.important) && (!onlyAttachments || n.attachments > 0)
    val notes: List<NoteItem> = when {
        view == "shared" && sharedFolder != null -> sharedItems ?: emptyList()
        view != null -> viewItems ?: emptyList()
        q.isNotBlank() -> (found ?: all.filter { it.title.contains(q.trim(), true) || it.snippet.contains(q.trim(), true) }).filter(::matches)
        filtering -> all.filter(::matches)
        else -> all.filter { it.folder == folder }
    }
    val subFolders = if (view == null && q.isBlank() && !filtering) folders.filter { it.parent == folder } else emptyList()

    // Rezept scannen (docs/rezept-scan-konzept.md): an Stelle der Liste, danach die neue Rezept-Notiz öffnen
    if (recipeScan) {
        RecipeScanScreen(folder, inTab = onClose == null) { id ->
            recipeScan = false
            if (id != null) scope.launch { refresh(); onOpen(id) }
        }
        return
    }

    // Als Reiter liegt die Seite schon im Hauptbildschirm, der die Systemleisten beruecksichtigt
    val insets = if (onClose == null) WindowInsets(0) else null
    Scaffold(
        contentWindowInsets = insets ?: ScaffoldDefaults.contentWindowInsets,
        topBar = {
            TabTopBar(title, if (view == null) search else null, insets = insets,
                // Als Reiter ohne Zurueck-Pfeil - ausser in einem Ordner, im Archiv oder Papierkorb
                navigationIcon = {
                    if (folder != null || view != null || inVault) IconButton(onClick = { up() }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) }
                    else if (onClose != null) IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) }
                },
                actions = {
                    if (view == null && !inVault) IconButton(onClick = { recipeScan = true }) {
                        Icon(Icons.Default.DocumentScanner, contentDescription = stringResource(R.string.recipe_scan))
                    }
                    IconButton(onClick = { scope.launch {
                        refresh()
                        view?.let { loadView(it) }
                        sharedFolder?.let { f -> sharedItems = try { repo.folderItems(f) } catch (e: Exception) { error = e.message; sharedItems } }
                    } }) {
                        Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.refresh))
                    }
                })
        },
        floatingActionButton = {
            if (view == null) FloatingActionButton(onClick = {
                if (creating) return@FloatingActionButton
                creating = true
                // Neue Notiz im Ordner, der gerade offen ist
                scope.launch { try { onOpen(repo.create(folder)) } catch (e: Exception) { error = e.message } finally { creating = false } }
            }) {
                if (creating) CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp)
                else Icon(Icons.Default.Add, contentDescription = stringResource(R.string.new_note))
            }
        }
    ) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            if (view == null) {
                Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilterChip(selected = onlyImportant, onClick = { onlyImportant = !onlyImportant }, label = { Text(stringResource(R.string.notes_important)) },
                        leadingIcon = { Icon(Icons.Default.Star, contentDescription = null, Modifier.size(18.dp)) })
                    FilterChip(selected = onlyAttachments, onClick = { onlyAttachments = !onlyAttachments }, label = { Text(stringResource(R.string.notes_with_attachment)) },
                        leadingIcon = { Icon(Icons.Default.AttachFile, contentDescription = null, Modifier.size(18.dp)) })
                    AssistChip(onClick = { view = "archive" }, label = { Text(stringResource(R.string.folder_archive)) },
                        leadingIcon = { Icon(Icons.Default.Archive, contentDescription = null, Modifier.size(18.dp)) })
                    AssistChip(onClick = { view = "trash" }, label = { Text(stringResource(R.string.folder_trash)) },
                        leadingIcon = { Icon(Icons.Default.Delete, contentDescription = null, Modifier.size(18.dp)) })
                    // Im Tresor gibt es keine Freigaben
                    if (!inVault) AssistChip(onClick = { view = "shared" }, label = { Text(stringResource(R.string.notes_shared_with_me)) },
                        leadingIcon = { Icon(Icons.Default.People, contentDescription = null, Modifier.size(18.dp)) })
                    if (vaultOn && !inVault) TresorChip { scope.launch { pickVault() } }
                }
            }
            if (inVault) Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.LockOpen, contentDescription = null, Modifier.size(16.dp), tint = TresorOpenColor)
                Text(stringResource(R.string.notes_tresor_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(start = 6.dp))
            }
            error?.let { Text(stringResource(R.string.error_generic, it), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp)) }
            // Geteilte Ordner: in der Übersicht die Freigaben, in einem Ordner seine Unterordner
            val sharedDirs: List<Pair<NoteShares.SharedFolder, String?>> = when {
                view != "shared" -> emptyList()
                sharedFolder == null -> sharedRoots.mapNotNull { r -> r.folders.firstOrNull { it.syncId == r.root }?.let { it to r.owner } }
                else -> sharedRoot?.folders?.filter { it.parent == sharedFolder }?.map { it to null } ?: emptyList()
            }
            val busy = loading && all.isEmpty() || (view != null && viewItems == null) || (sharedFolder != null && sharedItems == null)
            if (busy) Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            else if (notes.isEmpty() && subFolders.isEmpty() && sharedDirs.isEmpty()) Text(stringResource(R.string.no_notes), modifier = Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
                items(sharedDirs, key = { "s:" + it.first.syncId }) { (f, owner) ->
                    ListItem(
                        leadingContent = { Icon(Icons.Default.FolderShared, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                        headlineContent = { Text(f.name, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                        supportingContent = owner?.let { o -> { Text(o) } },
                        modifier = Modifier.clickable { sharedFolder = f.syncId }
                    )
                }
                if (sharedDirs.isNotEmpty() && notes.isNotEmpty()) item { HorizontalDivider() }
                items(subFolders, key = { "f:" + it.syncId }) { f ->
                    val inside = all.count { it.folder == f.syncId }
                    val sub = folders.count { it.parent == f.syncId }
                    ListItem(
                        leadingContent = { Icon(Icons.Default.Folder, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant) },
                        headlineContent = { Text(f.name, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                        supportingContent = {
                            Text(listOfNotNull(
                                if (inside > 0) pluralStringResource(R.plurals.n_notes, inside, inside) else null,
                                if (sub > 0) "$sub " + stringResource(R.string.folders) else null
                            ).joinToString(" · "))
                        },
                        modifier = Modifier.clickable { folder = f.syncId }
                    )
                }
                if (subFolders.isNotEmpty() && notes.isNotEmpty()) item { HorizontalDivider() }
                items(notes, key = { "n:" + it.id }) { n -> NoteRow(n, showFolder = filtering || q.isNotBlank() || (view == "shared" && sharedFolder == null),
                    folderName = if (view == "shared") owners[n.id] else folders.firstOrNull { it.syncId == n.folder }?.name) { repo.order = notes.map { it.id }; onOpen(n.id) } }
            }
        }
    }
    vaultChoice?.let { list ->
        AlertDialog(onDismissRequest = { vaultChoice = null },
            title = { Text(stringResource(R.string.notes_tresor_choose)) },
            text = {
                Column {
                    list.forEach { d ->
                        ListItem(headlineContent = { Text(d.name ?: d.folder) },
                            leadingContent = { Icon(Icons.Outlined.LockOpen, contentDescription = null, tint = TresorOpenColor) },
                            modifier = Modifier.clickable { vaultChoice = null; enterVault(d) })
                    }
                }
            },
            confirmButton = { TextButton(onClick = { vaultChoice = null }) { Text(stringResource(R.string.cancel)) } })
    }
}

@Composable
private fun NoteRow(n: NoteItem, showFolder: Boolean, folderName: String?, onClick: () -> Unit) {
    val ctx = LocalContext.current
    ListItem(
        // Senkrechter Balken in der Farbe der Notiz (wie im Web)
        leadingContent = { Box(Modifier.width(4.dp).height(40.dp).background(noteColor(n.color), RoundedCornerShape(2.dp))) },
        headlineContent = { Text(n.title.ifBlank { stringResource(R.string.note_untitled) }, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        supportingContent = {
            Column {
                if (n.snippet.isNotBlank()) Text(n.snippet, maxLines = 2, overflow = TextOverflow.Ellipsis)
                val meta = listOfNotNull(if (n.modified.isNotEmpty()) formatDate(ctx, n.modified) else null, if (showFolder) folderName else null)
                if (meta.isNotEmpty()) Text(meta.joinToString(" · "), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        },
        trailingContent = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (n.attachments > 0) Icon(Icons.Default.AttachFile, contentDescription = null, Modifier.size(18.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                if (n.important) Icon(Icons.Default.Star, contentDescription = null, tint = Color(0xFFF5B400))
            }
        },
        modifier = Modifier.clickable(onClick = onClick)
    )
}


/** Dieselben Notizfarben wie in notizi (ColorSwatchPanel). */
val NOTE_COLORS = listOf(
    "#FFC700", "#FFA000", "#FF7043", "#FF5C6C", "#EC407A", "#C77DFF", "#9575CD", "#7986CB",
    "#4DA3FF", "#29B6F6", "#26C6DA", "#3DC97A", "#8BC34A", "#A1887F", "#9AA5B1", "#5A6572"
)

/** Farbe einer Notiz (#rrggbb) - ohne Farbe das Gelb von Notizi. */
fun noteColor(hex: String?): Color =
    try { Color(android.graphics.Color.parseColor(hex ?: "#FFF9A8")) } catch (_: Exception) { Color(0xFFFFF9A8) }
