package at.dimes.dmoffice.ui

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import android.provider.Settings
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.outlined.Image
import androidx.compose.material.icons.outlined.InsertDriveFile
import androidx.compose.material.icons.outlined.Movie
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
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
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.background
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import at.dimes.dmoffice.R
import at.dimes.dmoffice.upload.AutoUpload
import at.dimes.dmoffice.upload.Scanner
import at.dimes.dmoffice.upload.UploadEntry
import at.dimes.dmoffice.upload.UploadLogic
import at.dimes.dmoffice.upload.UploadRule
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId

/** Zugriff auf Fotos und Videos: ganz, nur ausgewaehlte (Android 14+) oder keiner. */
internal enum class MediaAccess { FULL, PARTIAL, NONE }

internal object MediaPermissions {
    fun state(ctx: Context): MediaAccess {
        fun has(p: String) = ContextCompat.checkSelfPermission(ctx, p) == PackageManager.PERMISSION_GRANTED
        return when {
            Build.VERSION.SDK_INT >= 33 && has(Manifest.permission.READ_MEDIA_IMAGES) && has(Manifest.permission.READ_MEDIA_VIDEO) -> MediaAccess.FULL
            Build.VERSION.SDK_INT >= 34 && has(Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED) -> MediaAccess.PARTIAL
            Build.VERSION.SDK_INT >= 33 && (has(Manifest.permission.READ_MEDIA_IMAGES) || has(Manifest.permission.READ_MEDIA_VIDEO)) -> MediaAccess.PARTIAL
            Build.VERSION.SDK_INT < 33 && has(Manifest.permission.READ_EXTERNAL_STORAGE) -> MediaAccess.FULL
            else -> MediaAccess.NONE
        }
    }

    /** Was fuer das automatische Hochladen angefragt wird; [delete]: auf Android 10 und aelter auch Schreiben. */
    fun request(delete: Boolean = false): Array<String> = buildList {
        if (Build.VERSION.SDK_INT >= 33) { add(Manifest.permission.READ_MEDIA_IMAGES); add(Manifest.permission.READ_MEDIA_VIDEO) }
        else add(Manifest.permission.READ_EXTERNAL_STORAGE)
        if (Build.VERSION.SDK_INT >= 34) add(Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED)
        if (Build.VERSION.SDK_INT >= 29) add(Manifest.permission.ACCESS_MEDIA_LOCATION)
        if (delete && Build.VERSION.SDK_INT <= 29) add(Manifest.permission.WRITE_EXTERNAL_STORAGE)
        if (Build.VERSION.SDK_INT >= 33) add(Manifest.permission.POST_NOTIFICATIONS)
    }.toTypedArray()

    fun openAppSettings(ctx: Context) {
        ctx.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", ctx.packageName, null)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}

/** Nach jeder Rueckkehr in die App (z. B. aus den Android-Einstellungen) neu ermitteln. */
@Composable
private fun rememberMediaAccess(): Pair<MediaAccess, () -> Unit> {
    val ctx = LocalContext.current
    var state by remember { mutableStateOf(MediaPermissions.state(ctx)) }
    val owner = LocalLifecycleOwner.current
    LaunchedEffect(owner) { owner.lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) { state = MediaPermissions.state(ctx) } }
    return state to { state = MediaPermissions.state(ctx) }
}

private fun sourceName(ctx: Context, source: String): String = ctx.getString(when (source) {
    UploadLogic.SRC_CAMERA -> R.string.au_src_camera
    UploadLogic.SRC_SCREENSHOTS -> R.string.au_src_screenshots
    UploadLogic.SRC_WHATSAPP -> R.string.au_src_whatsapp
    UploadLogic.SRC_MEDIA -> R.string.au_src_media
    else -> R.string.au_src_tree
})

private fun subfolderName(ctx: Context, s: String): String = when (s) {
    UploadLogic.SUB_YEAR -> ctx.getString(R.string.au_sub_year)
    UploadLogic.SUB_MONTH -> ctx.getString(R.string.au_sub_month)
    UploadLogic.SUB_DAY -> ctx.getString(R.string.au_sub_day)
    else -> ctx.getString(R.string.au_sub_none)
}

private fun typesName(ctx: Context, t: String, tree: Boolean): String = ctx.getString(when (t) {
    UploadLogic.TYPES_PHOTOS -> R.string.au_types_photos
    UploadLogic.TYPES_VIDEOS -> R.string.au_types_videos
    else -> if (tree) R.string.au_types_all_files else R.string.au_types_all
})

private fun conflictName(ctx: Context, c: String): String = ctx.getString(when (c) {
    UploadLogic.CONFLICT_SKIP -> R.string.au_conflict_skip
    UploadLogic.CONFLICT_REPLACE -> R.string.au_conflict_replace
    else -> R.string.au_conflict_rename
})

/** Kurzbeschreibung einer Regel: "Kamera → /Fotos · nach Monat · nur WLAN". */
private fun ruleSummary(ctx: Context, r: UploadRule): String = listOfNotNull(
    r.sourceLabel + " → /" + r.target,
    subfolderName(ctx, r.subfolders).takeIf { r.subfolders != UploadLogic.SUB_NONE },
    typesName(ctx, r.types, r.source == UploadLogic.SRC_TREE),
    ctx.getString(R.string.au_wifi_short).takeIf { r.wifiOnly },
    ctx.getString(R.string.au_charging_short).takeIf { r.chargingOnly },
    ctx.getString(R.string.au_delete_short).takeIf { r.deleteAfter },
).joinToString(" · ")

// ======================================================================
//  Uebersicht: Regeln
// ======================================================================

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AutoUploadScreen(onRule: (Long) -> Unit, onQueue: () -> Unit, onFreeUp: () -> Unit, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val dao = remember { AutoUpload.dao(ctx) }
    val rules by dao.rules().collectAsState(initial = null)
    val counts by dao.counts().collectAsState(initial = emptyList())
    val perRule by dao.ruleCounts().collectAsState(initial = emptyList())
    val deletes by dao.deletePending().collectAsState(initial = emptyList())
    val errors by AutoUpload.ruleErrors.collectAsState()
    val paused by AutoUpload.paused.collectAsState()
    val running by AutoUpload.running.collectAsState()
    val blocked by AutoUpload.blocked.collectAsState()
    val (access, recheck) = rememberMediaAccess()
    val perm = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { recheck(); AutoUpload.kick(ctx) }
    val queued = counts.firstOrNull { it.status == UploadEntry.QUEUED }?.n ?: 0
    val failed = counts.firstOrNull { it.status == UploadEntry.FAILED }?.n ?: 0
    val needsMedia = rules?.any { it.enabled && it.source != UploadLogic.SRC_TREE } == true

    Scaffold(
        topBar = {
            TopAppBar(title = { Text(stringResource(R.string.au_title)) },
                navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
                actions = {
                    IconButton(onClick = { AutoUpload.kick(ctx) }) { Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.au_scan_now)) }
                    IconButton(onClick = onQueue) { Icon(Icons.AutoMirrored.Filled.List, contentDescription = stringResource(R.string.au_queue)) }
                })
        },
        floatingActionButton = {
            ExtendedFloatingActionButton(onClick = { onRule(0) }, icon = { Icon(Icons.Default.Add, contentDescription = null) }, text = { Text(stringResource(R.string.au_add_rule)) })
        }
    ) { pad ->
        Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 96.dp)) {
            if (needsMedia && access != MediaAccess.FULL) Notice(
                stringResource(if (access == MediaAccess.PARTIAL) R.string.au_perm_partial else R.string.au_perm_missing), error = true,
                action = stringResource(R.string.au_perm_grant)) {
                if (access == MediaAccess.PARTIAL) MediaPermissions.openAppSettings(ctx) else perm.launch(MediaPermissions.request())
            }
            blocked?.let { Notice(it, error = true, action = stringResource(R.string.au_retry_now)) { scope.launch { AutoUpload.retryAll(ctx) } } }
            if (paused) Notice(stringResource(R.string.au_paused), action = stringResource(R.string.au_resume)) { AutoUpload.setPaused(ctx, false) }
            if (!rules.isNullOrEmpty()) FreeUpCard(onFreeUp)

            // Stand der Warteschlange
            ListItem(modifier = Modifier.clickable(onClick = onQueue),
                headlineContent = { Text(stringResource(R.string.au_queue)) },
                supportingContent = {
                    Text(listOfNotNull(
                        if (running) stringResource(R.string.au_running) else null,
                        pluralStringResource(R.plurals.au_n_waiting, queued, queued),
                        if (failed > 0) pluralStringResource(R.plurals.au_n_failed, failed, failed) else null,
                        if (deletes.isNotEmpty()) pluralStringResource(R.plurals.au_n_delete, deletes.size, deletes.size) else null,
                    ).joinToString(" · "), color = if (failed > 0) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
                },
                trailingContent = { Icon(Icons.AutoMirrored.Filled.List, contentDescription = null) })
            HorizontalDivider()

            SectionHeader(stringResource(R.string.au_rules))
            val list = rules
            if (list != null && list.isEmpty()) Text(stringResource(R.string.au_empty), modifier = Modifier.padding(16.dp), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            for (r in list ?: emptyList()) {
                ListItem(modifier = Modifier.clickable { onRule(r.id) },
                    headlineContent = { Text(r.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    supportingContent = {
                        Column {
                            Text(ruleSummary(ctx, r), style = MaterialTheme.typography.bodySmall)
                            RuleState(r, perRule.filter { it.ruleId == r.id }, running)
                            errors[r.id]?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }
                            if (r.enabled) AutoUpload.waitReason(ctx, r)?.let { Text(stringResource(it), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.tertiary) }
                        }
                    },
                    trailingContent = {
                        Switch(checked = r.enabled, onCheckedChange = { on ->
                            if (on && r.source != UploadLogic.SRC_TREE && access == MediaAccess.NONE) perm.launch(MediaPermissions.request(r.deleteAfter))
                            scope.launch { dao.saveRule(r.copy(enabled = on)); AutoUpload.refresh(ctx) }
                        })
                    })
            }
            Text(stringResource(R.string.au_hint), modifier = Modifier.padding(16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

/** Abschnittsueberschrift innerhalb eines schon eingerueckten Formularblocks. */
@Composable
private fun FormHeader(text: String) {
    Text(text, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 14.dp))
}

@Composable
private fun Notice(text: String, error: Boolean = false, action: String? = null, onAction: () -> Unit = {}) {
    Card(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
        colors = CardDefaults.cardColors(containerColor = if (error) MaterialTheme.colorScheme.errorContainer else MaterialTheme.colorScheme.secondaryContainer)) {
        Column(Modifier.padding(12.dp)) {
            Text(text, style = MaterialTheme.typography.bodyMedium, color = if (error) MaterialTheme.colorScheme.onErrorContainer else MaterialTheme.colorScheme.onSecondaryContainer)
            if (action != null) TextButton(onClick = onAction, modifier = Modifier.align(Alignment.End)) { Text(action) }
        }
    }
}

// ======================================================================
//  Regel bearbeiten
// ======================================================================

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun UploadRuleScreen(id: Long, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val dao = remember { AutoUpload.dao(ctx) }
    var loaded by remember { mutableStateOf<UploadRule?>(null) }
    var r by remember { mutableStateOf(UploadRule(name = ctx.getString(R.string.au_src_camera), source = UploadLogic.SRC_CAMERA, sourcePaths = UploadLogic.PRESET_PATHS.getValue(UploadLogic.SRC_CAMERA).joinToString("\n"),
        sourceLabel = ctx.getString(R.string.au_src_camera), target = ctx.getString(R.string.au_default_target_camera), sinceMs = System.currentTimeMillis())) }
    var nameTouched by remember { mutableStateOf(false) }
    /** Ziel schon selbst gewaehlt: die Wahl der Quelle schlaegt dann keinen Zielordner mehr vor. */
    var targetTouched by remember { mutableStateOf(false) }
    // "Welche Fotos hochladen": alle (sinceMs = 0) oder ab einem Datum (anfangs heute)
    val which = if (r.sinceMs == 0L) WHICH_ALL else WHICH_DATE
    // Wie viele Fotos und Videos jetzt in der Quelle liegen und wie viele davon vor dem gewaehlten Datum
    var totalCount by remember { mutableStateOf<Int?>(null) }
    var olderCount by remember { mutableStateOf<Int?>(null) }
    // "Alle" waehlen: nachfragen - dann geht womoeglich sehr viel auf einmal hoch
    var confirmAll by remember { mutableStateOf<Int?>(null) }
    LaunchedEffect(r.source, r.sourcePaths, r.types) {
        totalCount = null
        totalCount = try { withContext(Dispatchers.IO) { AutoUpload.countExisting(ctx, r.copy(sinceMs = System.currentTimeMillis())) } } catch (_: Exception) { null }
    }
    LaunchedEffect(r.source, r.sourcePaths, r.types, r.sinceMs) {
        olderCount = null
        if (r.sinceMs > 0) olderCount = try { withContext(Dispatchers.IO) { AutoUpload.countExisting(ctx, r) } } catch (_: Exception) { null }
    }
    var pickTarget by remember { mutableStateOf(false) }
    var mediaFolders by remember { mutableStateOf<List<Scanner.MediaFolder>?>(null) }
    var confirmDelete by remember { mutableStateOf(false) }
    var confirmRemoveRule by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val (access, recheck) = rememberMediaAccess()

    LaunchedEffect(id) {
        if (id != 0L) dao.rule(id)?.let { loaded = it; r = it; nameTouched = true }
    }

    val perm = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { recheck() }
    val tree = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) {
            // Dauerhaft behalten; Schreiben fuer "nach dem Hochladen loeschen"
            try { ctx.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION) }
            catch (_: SecurityException) { try { ctx.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION) } catch (_: SecurityException) { } }
            val label = Scanner.treeLabel(ctx, uri)
            r = r.copy(source = UploadLogic.SRC_TREE, sourcePaths = uri.toString(), sourceLabel = label, types = UploadLogic.TYPES_ALL,
                name = if (nameTouched) r.name else label.substringAfterLast('/'), target = if (loaded == null && !targetTouched) label.substringAfterLast('/') else r.target)
        }
    }

    fun chooseSource(src: String) {
        when (src) {
            UploadLogic.SRC_TREE -> tree.launch(null)
            UploadLogic.SRC_MEDIA -> {
                if (access == MediaAccess.NONE) perm.launch(MediaPermissions.request(r.deleteAfter))
                scope.launch { mediaFolders = withContext(Dispatchers.IO) { Scanner.mediaFolders(ctx) } }
            }
            else -> {
                if (access == MediaAccess.NONE) perm.launch(MediaPermissions.request(r.deleteAfter))
                val label = sourceName(ctx, src)
                val target = ctx.getString(when (src) { UploadLogic.SRC_SCREENSHOTS -> R.string.au_default_target_screenshots; UploadLogic.SRC_WHATSAPP -> R.string.au_default_target_whatsapp; else -> R.string.au_default_target_camera })
                r = r.copy(source = src, sourcePaths = UploadLogic.PRESET_PATHS.getValue(src).joinToString("\n"), sourceLabel = label,
                    name = if (nameTouched) r.name else label, target = if (loaded == null && !targetTouched) target else r.target, types = if (r.source == UploadLogic.SRC_TREE) UploadLogic.TYPES_ALL else r.types)
            }
        }
    }

    fun save() {
        error = null
        val target = UploadLogic.normalize(r.target)
        if (UploadLogic.forbiddenTarget(target)) { error = ctx.getString(R.string.au_err_forbidden); return }
        if (r.sourcePaths.isBlank()) { error = ctx.getString(R.string.au_err_no_source); return }
        val rule = r.copy(target = target, name = r.name.trim().ifEmpty { r.sourceLabel })
        if (rule.enabled && rule.source != UploadLogic.SRC_TREE && access == MediaAccess.NONE) perm.launch(MediaPermissions.request(rule.deleteAfter))
        else if (rule.deleteAfter && Build.VERSION.SDK_INT <= 29 && ContextCompat.checkSelfPermission(ctx, Manifest.permission.WRITE_EXTERNAL_STORAGE) != PackageManager.PERMISSION_GRANTED)
            perm.launch(MediaPermissions.request(true))
        scope.launch {
            dao.saveRule(rule)
            // Wartendes dieser Regel neu bestimmen (Ziel, Arten, Startdatum koennen sich geaendert haben)
            if (rule.id != 0L) dao.deleteQueued(rule.id)
            // Loeschen nachtraeglich eingeschaltet: auch das schon Hochgeladene dieser Regel vom Geraet nehmen
            // (geloescht wird nur, was noch genau so gross ist wie beim Hochladen - siehe deletable)
            if (rule.id != 0L && rule.deleteAfter && loaded?.deleteAfter == false) dao.markDoneForDelete(rule.id)
            AutoUpload.refresh(ctx)
            onClose()
        }
    }

    Scaffold(topBar = {
        TopAppBar(title = { Text(stringResource(if (id == 0L) R.string.au_new_rule else R.string.au_edit_rule)) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                if (loaded != null) IconButton(onClick = { confirmRemoveRule = true }) { Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.delete)) }
                IconButton(onClick = { save() }) { Icon(Icons.Default.Check, contentDescription = stringResource(R.string.save)) }
            })
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 32.dp)) {
            val tree = r.source == UploadLogic.SRC_TREE
            ListItem(headlineContent = { Text(stringResource(R.string.au_enabled)) }, trailingContent = { Switch(checked = r.enabled, onCheckedChange = { r = r.copy(enabled = it) }) })
            Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedTextField(value = r.name, onValueChange = { r = r.copy(name = it); nameTouched = true }, label = { Text(stringResource(R.string.files_name)) }, singleLine = true, modifier = Modifier.fillMaxWidth())

                FormHeader(stringResource(R.string.au_source))
                Picker(stringResource(R.string.au_source), r.source, listOf(UploadLogic.SRC_CAMERA, UploadLogic.SRC_SCREENSHOTS, UploadLogic.SRC_WHATSAPP, UploadLogic.SRC_MEDIA, UploadLogic.SRC_TREE),
                    { sourceName(ctx, it) }, { chooseSource(it) })
                Text(if (tree || r.source == UploadLogic.SRC_MEDIA) r.sourceLabel else r.sourceLabel + "\n" + Scanner.mediaPaths(r).joinToString(", "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (!tree && access != MediaAccess.FULL) Text(stringResource(if (access == MediaAccess.PARTIAL) R.string.au_perm_partial else R.string.au_perm_missing),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)

                FormHeader(stringResource(R.string.au_target))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(value = r.target, onValueChange = { r = r.copy(target = it); targetTouched = true }, label = { Text(stringResource(R.string.au_target_folder)) }, singleLine = true,
                        prefix = { Text("/") }, modifier = Modifier.weight(1f), isError = UploadLogic.forbiddenTarget(r.target))
                    IconButton(onClick = { pickTarget = true }) { Icon(Icons.Default.Folder, contentDescription = stringResource(R.string.au_choose_folder)) }
                }
                Picker(stringResource(R.string.au_subfolders), r.subfolders, listOf(UploadLogic.SUB_NONE, UploadLogic.SUB_YEAR, UploadLogic.SUB_MONTH, UploadLogic.SUB_DAY),
                    { subfolderName(ctx, it) }, { r = r.copy(subfolders = it) })
                val example = UploadLogic.targetDir(r.target, if (r.keepStructure) "…" else "", r.keepStructure, r.subfolders, System.currentTimeMillis())
                Text(stringResource(R.string.au_example, "/$example/IMG_1234.jpg"), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            ListItem(headlineContent = { Text(stringResource(R.string.au_keep_structure)) },
                trailingContent = { Switch(checked = r.keepStructure, onCheckedChange = { r = r.copy(keepStructure = it) }) })

            Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                FormHeader(stringResource(R.string.au_files))
                Picker(stringResource(R.string.au_types), r.types, listOf(UploadLogic.TYPES_ALL, UploadLogic.TYPES_PHOTOS, UploadLogic.TYPES_VIDEOS),
                    { typesName(ctx, it, tree) }, { r = r.copy(types = it) })
                Picker(stringResource(R.string.au_conflict), r.conflict, listOf(UploadLogic.CONFLICT_RENAME, UploadLogic.CONFLICT_SKIP, UploadLogic.CONFLICT_REPLACE),
                    { conflictName(ctx, it) }, { r = r.copy(conflict = it) })
                ListItem(headlineContent = { Text(stringResource(R.string.au_skip_duplicates)) },
                    trailingContent = { Switch(checked = r.skipDuplicates, onCheckedChange = { r = r.copy(skipDuplicates = it) }) })
                Picker(stringResource(R.string.au_after), r.deleteAfter, listOf(false, true),
                    { ctx.getString(if (it) R.string.au_after_delete else R.string.au_after_keep) }, { if (it && !r.deleteAfter) confirmDelete = true else r = r.copy(deleteAfter = it) })
                if (r.deleteAfter) Text(stringResource(if (!AutoUpload.allFilesAccess() && !tree) R.string.au_delete_warning_confirm else R.string.au_delete_warning),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
                if (r.deleteAfter && !tree && !AutoUpload.allFilesAccess()) AllFilesAccessHint()
            }

            SectionHeader(stringResource(R.string.au_when))
            ListItem(headlineContent = { Text(stringResource(R.string.au_wifi_only)) },
                trailingContent = { Switch(checked = r.wifiOnly, onCheckedChange = { r = r.copy(wifiOnly = it) }) })
            ListItem(headlineContent = { Text(stringResource(R.string.au_roaming)) },
                trailingContent = { Switch(checked = r.roaming, enabled = !r.wifiOnly, onCheckedChange = { r = r.copy(roaming = it) }) })
            ListItem(headlineContent = { Text(stringResource(R.string.au_charging_only)) },
                trailingContent = { Switch(checked = r.chargingOnly, onCheckedChange = { r = r.copy(chargingOnly = it) }) })
            Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                FormHeader(stringResource(R.string.au_which))
                // Ein Datum (anfangs heute) oder "Alle" - mehr braucht es nicht
                val zone = ZoneId.systemDefault()
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(R.string.au_since), modifier = Modifier.weight(1f))
                    if (which == WHICH_ALL) {
                        Text(stringResource(R.string.au_which_all), style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary)
                        TextButton(onClick = { r = r.copy(sinceMs = LocalDate.now().atStartOfDay(zone).toInstant().toEpochMilli()) }) { Text(stringResource(R.string.au_which_date)) }
                    } else {
                        DateButton(Instant.ofEpochMilli(r.sinceMs).atZone(zone).toLocalDate(), { d: LocalDate -> r = r.copy(sinceMs = d.atStartOfDay(zone).toInstant().toEpochMilli()) })
                        TextButton(onClick = { val c = totalCount; if (c != null && c > 0) confirmAll = c else r = r.copy(sinceMs = 0L) }) { Text(stringResource(R.string.au_which_all)) }
                    }
                }
                // Wie viele Fotos und Videos die Wahl betrifft
                val total = totalCount
                val older = olderCount
                if (total != null && total > 0) Text(when (which) {
                    WHICH_ALL -> pluralStringResource(R.plurals.au_which_all_count, total, total)
                    else -> if (older == null) "" else pluralStringResource(R.plurals.au_which_date_count, total - older, total - older, older)
                }, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            error?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
            Row(Modifier.padding(16.dp)) { Button(onClick = { save() }) { Text(stringResource(R.string.save)) } }
        }
    }

    if (pickTarget) FolderPickerDialog(start = UploadLogic.normalize(r.target), exclude = null,
        onDismiss = { pickTarget = false }, title = stringResource(R.string.au_target_folder), confirm = stringResource(R.string.au_upload_here)) { dir ->
        pickTarget = false
        r = r.copy(target = dir)
        targetTouched = true
    }

    mediaFolders?.let { folders ->
        MediaFolderPicker(folders, onDismiss = { mediaFolders = null }) { path ->
            mediaFolders = null
            val label = UploadLogic.normalize(path)
            r = r.copy(source = UploadLogic.SRC_MEDIA, sourcePaths = path, sourceLabel = label, name = if (nameTouched) r.name else label.substringAfterLast('/'),
                target = if (loaded == null && !targetTouched) label.substringAfterLast('/') else r.target, types = if (r.source == UploadLogic.SRC_TREE) UploadLogic.TYPES_ALL else r.types)
        }
    }



    confirmAll?.let { n ->
        AlertDialog(onDismissRequest = { confirmAll = null }, title = { Text(stringResource(R.string.au_all_title)) },
            text = { Text(pluralStringResource(R.plurals.au_all_text, n, n, r.sourceLabel)) },
            confirmButton = { TextButton(onClick = { confirmAll = null; r = r.copy(sinceMs = 0L) }) { Text(pluralStringResource(R.plurals.au_all_yes, n, n)) } },
            dismissButton = { TextButton(onClick = { confirmAll = null }) { Text(stringResource(R.string.cancel)) } })
    }

    if (confirmDelete) AlertDialog(onDismissRequest = { confirmDelete = false }, title = { Text(stringResource(R.string.au_after_delete)) },
        text = { Text(stringResource(R.string.au_delete_confirm)) },
        confirmButton = { TextButton(onClick = { confirmDelete = false; r = r.copy(deleteAfter = true) }) { Text(stringResource(R.string.au_delete_yes), color = MaterialTheme.colorScheme.error) } },
        dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text(stringResource(R.string.cancel)) } })

    if (confirmRemoveRule) AlertDialog(onDismissRequest = { confirmRemoveRule = false }, title = { Text(stringResource(R.string.au_remove_rule)) },
        text = { Text(stringResource(R.string.au_remove_rule_text)) },
        confirmButton = { TextButton(onClick = { confirmRemoveRule = false; loaded?.let { l -> scope.launch { AutoUpload.deleteRule(ctx, l); onClose() } } }) { Text(stringResource(R.string.delete)) } },
        dismissButton = { TextButton(onClick = { confirmRemoveRule = false }) { Text(stringResource(R.string.cancel)) } })
}

// ======================================================================
//  Warteschlange und Verlauf
// ======================================================================

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun UploadQueueScreen(onClose: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val dao = remember { AutoUpload.dao(ctx) }
    val pending by dao.pending().collectAsState(initial = emptyList())
    val failed by dao.failed().collectAsState(initial = emptyList())
    val finished by dao.finished().collectAsState(initial = emptyList())
    val deletes by dao.deletePending().collectAsState(initial = emptyList())
    val rules by dao.rules().collectAsState(initial = emptyList())
    val progress by AutoUpload.progress.collectAsState()
    val paused by AutoUpload.paused.collectAsState()
    val blocked by AutoUpload.blocked.collectAsState()
    var tab by rememberSaveable { mutableIntStateOf(0) }
    var menu by remember { mutableStateOf(false) }
    var deleteIds by remember { mutableStateOf<List<Long>>(emptyList()) }

    // Loeschen fremder Medien ab Android 11: Systemdialog, danach als geloescht merken
    val deleteRequest = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) { res ->
        val ids = deleteIds; deleteIds = emptyList()
        if (res.resultCode == Activity.RESULT_OK) scope.launch { AutoUpload.markDeleted(ctx, ids) }
    }
    fun deleteNow() {
        scope.launch {
            val list = AutoUpload.deletable(ctx)
            if (list.isEmpty()) return@launch
            if (Build.VERSION.SDK_INT >= 30) {
                deleteIds = list.map { it.id }
                val pi = MediaStore.createDeleteRequest(ctx.contentResolver, list.map { Uri.parse(it.uri) })
                deleteRequest.launch(IntentSenderRequest.Builder(pi.intentSender).build())
            } else {
                val done = withContext(Dispatchers.IO) { list.filter { e -> try { ctx.contentResolver.delete(Uri.parse(e.uri), null, null) > 0 } catch (_: Exception) { false } } }
                AutoUpload.markDeleted(ctx, done.map { it.id })
            }
        }
    }

    BackHandler { onClose() }
    Scaffold(topBar = {
        TopAppBar(title = { Text(stringResource(R.string.au_queue)) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                IconButton(onClick = { AutoUpload.setPaused(ctx, !paused) }) {
                    Icon(if (paused) Icons.Default.PlayArrow else Icons.Default.Pause, contentDescription = stringResource(if (paused) R.string.au_resume else R.string.au_pause))
                }
                IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = null) }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    DropdownMenuItem(text = { Text(stringResource(R.string.au_retry_now)) }, onClick = { menu = false; scope.launch { AutoUpload.retryAll(ctx) } })
                    DropdownMenuItem(text = { Text(stringResource(R.string.au_clear_history)) }, onClick = { menu = false; scope.launch { dao.hideFinished() } })
                }
            })
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            if (paused) Notice(stringResource(R.string.au_paused), action = stringResource(R.string.au_resume)) { AutoUpload.setPaused(ctx, false) }
            blocked?.let { Notice(it, error = true) }
            if (deletes.isNotEmpty()) Card(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer)) {
                Column(Modifier.padding(12.dp)) {
                    Text(pluralStringResource(R.plurals.au_notif_delete, deletes.size, deletes.size), style = MaterialTheme.typography.bodyMedium)
                    Text(stringResource(R.string.au_delete_explain), style = MaterialTheme.typography.bodySmall)
                    Row(Modifier.align(Alignment.End)) {
                        TextButton(onClick = { scope.launch { dao.keepLocal(deletes.map { it.id }) } }) { Text(stringResource(R.string.au_keep)) }
                        Spacer(Modifier.width(8.dp))
                        Button(onClick = { deleteNow() }) { Text(stringResource(R.string.au_delete_now)) }
                    }
                    if (!AutoUpload.allFilesAccess()) AllFilesAccessHint()
                }
            }
            TabRow(selectedTabIndex = tab) {
                Tab(selected = tab == 0, onClick = { tab = 0 }, text = { Text(stringResource(R.string.au_tab_queue, pending.size)) })
                Tab(selected = tab == 1, onClick = { tab = 1 }, text = { Text(stringResource(R.string.au_tab_failed, failed.size)) })
                Tab(selected = tab == 2, onClick = { tab = 2 }, text = { Text(stringResource(R.string.au_tab_done)) })
            }
            val list = when (tab) { 0 -> pending; 1 -> failed; else -> finished }
            val ruleMap = rules.associateBy { it.id }
            if (list.isEmpty()) Text(stringResource(R.string.au_list_empty), modifier = Modifier.padding(24.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
            LazyColumn(Modifier.fillMaxSize()) {
                items(list, key = { it.id }) { e ->
                    EntryRow(e, ruleMap[e.ruleId], progress?.takeIf { it.entryId == e.id },
                        onCancel = { scope.launch { AutoUpload.cancel(ctx, e.id) } },
                        onRetry = { scope.launch { AutoUpload.retry(ctx, e.id) } })
                    HorizontalDivider()
                }
            }
        }
    }
}

@Composable
private fun EntryRow(e: UploadEntry, rule: UploadRule?, p: AutoUpload.Progress?, onCancel: () -> Unit, onRetry: () -> Unit) {
    val ctx = LocalContext.current
    val icon = when (UploadLogic.kindOf(e.mime, e.name)) { 1 -> Icons.Outlined.Image; 2 -> Icons.Outlined.Movie; else -> Icons.Outlined.InsertDriveFile }
    ListItem(
        leadingContent = { Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
        headlineContent = { Text(e.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        supportingContent = {
            Column {
                Text("→ /" + (e.remotePath?.substringBeforeLast('/', "") ?: e.targetDir) + " · " + sizeText(e.size), style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (p != null) {
                    LinearProgressIndicator(progress = { if (p.total > 0) p.done.toFloat() / p.total else 0f }, modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp))
                    Text(stringResource(R.string.au_bytes_of, sizeText(p.done), sizeText(p.total)), style = MaterialTheme.typography.bodySmall)
                }
                val status = when (e.status) {
                    UploadEntry.QUEUED -> when {
                        e.nextTry > System.currentTimeMillis() -> stringResource(R.string.au_retry_at, Fmt.dateTime(ctx, e.nextTry, false))
                        rule != null && !rule.enabled -> stringResource(R.string.au_rule_off)
                        rule != null -> AutoUpload.waitReason(ctx, rule)?.let { stringResource(it) }
                        else -> null
                    }
                    UploadEntry.DONE -> listOfNotNull(
                        stringResource(if (e.already) R.string.au_status_already else R.string.au_status_done, Fmt.dateTime(ctx, e.finished, false)),
                        stringResource(R.string.au_status_deleted).takeIf { e.localDeleted }).joinToString(" · ")
                    UploadEntry.CANCELLED -> stringResource(R.string.au_status_cancelled)
                    UploadEntry.SKIPPED -> stringResource(R.string.au_status_skipped)
                    else -> null
                }
                status?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                e.error?.takeIf { e.status == UploadEntry.FAILED || e.status == UploadEntry.QUEUED }?.let {
                    Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error, maxLines = 3, overflow = TextOverflow.Ellipsis)
                }
            }
        },
        trailingContent = {
            Row {
                if (e.status == UploadEntry.FAILED || e.status == UploadEntry.CANCELLED || e.status == UploadEntry.SKIPPED)
                    IconButton(onClick = onRetry) { Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.au_retry)) }
                if (e.status == UploadEntry.QUEUED || e.status == UploadEntry.RUNNING || e.status == UploadEntry.FAILED)
                    IconButton(onClick = onCancel) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.au_cancel)) }
            }
        })
}


/**
 * Auswahl "anderer Medienordner" als Vollbild-Seite: Ordnername fett, darunter der verkuerzte Pfad, rechts Anzahl und
 * das neueste Bild. Bekannte Ordner (Kamera, Screenshots, WhatsApp) zuerst, dann nach Anzahl; Kopien im Speicher
 * fremder Apps (Android/media, Android/data) erst auf Wunsch. Oben eine Suche.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun MediaFolderPicker(folders: List<Scanner.MediaFolder>, onDismiss: () -> Unit, onPick: (String) -> Unit) {
    var query by remember { mutableStateOf("") }
    var showForeign by remember { mutableStateOf(false) }
    val q = query.trim().lowercase()
    val matching = folders.filter { q.isEmpty() || it.path.lowercase().contains(q) }
    val (own, foreign) = matching.partition { UploadLogic.folderRank(it.path) < 2 }
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss, properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        androidx.compose.material3.Surface(Modifier.fillMaxSize()) {
            Column(Modifier.fillMaxSize().systemBarsPadding()) {
                TopAppBar(title = { Text(stringResource(R.string.au_src_media_title)) }, windowInsets = androidx.compose.foundation.layout.WindowInsets(0),
                    navigationIcon = { IconButton(onClick = onDismiss) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } })
                OutlinedTextField(value = query, onValueChange = { query = it }, singleLine = true, modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
                    placeholder = { Text(stringResource(R.string.au_folder_search)) },
                    leadingIcon = { Icon(androidx.compose.material.icons.Icons.Default.Search, contentDescription = null) })
                if (folders.isEmpty()) Text(stringResource(R.string.au_no_media_folders), modifier = Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
                LazyColumn(Modifier.fillMaxSize(), contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 96.dp)) {
                    items(own, key = { it.path }) { f -> MediaFolderRow(f) { onPick(f.path) } }
                    if (foreign.isNotEmpty()) {
                        item(key = "foreign") {
                            ListItem(modifier = Modifier.clickable { showForeign = !showForeign },
                                headlineContent = { Text(stringResource(R.string.au_foreign_folders, foreign.size), color = MaterialTheme.colorScheme.primary) },
                                supportingContent = { Text(stringResource(R.string.au_foreign_folders_hint)) },
                                trailingContent = { Icon(if (showForeign) Icons.Default.ExpandLess else Icons.Default.ExpandMore, contentDescription = null) })
                        }
                        if (showForeign || q.isNotEmpty()) items(foreign, key = { it.path }) { f -> MediaFolderRow(f) { onPick(f.path) } }
                    }
                }
            }
        }
    }
}

@Composable
private fun MediaFolderRow(f: Scanner.MediaFolder, onClick: () -> Unit) {
    val ctx = LocalContext.current
    val thumb by androidx.compose.runtime.produceState<android.graphics.Bitmap?>(null, f.latest) {
        value = f.latest?.let { u -> withContext(Dispatchers.IO) { Scanner.thumbnail(ctx, u) } }
    }
    val parent = UploadLogic.folderParent(f.path)
    ListItem(modifier = Modifier.clickable(onClick = onClick),
        leadingContent = { Icon(Icons.Default.Folder, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
        headlineContent = { Text(UploadLogic.folderName(f.path), fontWeight = androidx.compose.ui.text.font.FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        supportingContent = if (parent.isNotEmpty()) ({ Text(parent, maxLines = 1, overflow = TextOverflow.Ellipsis, color = MaterialTheme.colorScheme.onSurfaceVariant) }) else null,
        trailingContent = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(f.count.toString(), style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(end = 8.dp))
                val b = thumb
                if (b != null) androidx.compose.foundation.Image(b.asImageBitmap(), contentDescription = null, contentScale = androidx.compose.ui.layout.ContentScale.Crop,
                    modifier = Modifier.size(48.dp).clip(androidx.compose.foundation.shape.RoundedCornerShape(6.dp)))
                else androidx.compose.foundation.layout.Box(Modifier.size(48.dp).clip(androidx.compose.foundation.shape.RoundedCornerShape(6.dp)).background(MaterialTheme.colorScheme.surfaceVariant))
            }
        })
}


/** Stand einer Regel: wie viele hochgeladen, wartend, fehlgeschlagen; seit wann; warum gerade nichts passiert. */
@Composable
private fun RuleState(r: UploadRule, counts: List<at.dimes.dmoffice.upload.RuleCount>, running: Boolean) {
    val ctx = LocalContext.current
    fun n(vararg st: Int) = counts.filter { it.status in st }.sumOf { it.n }
    val done = n(UploadEntry.DONE); val waiting = n(UploadEntry.QUEUED, UploadEntry.RUNNING); val failed = n(UploadEntry.FAILED); val skipped = n(UploadEntry.SKIPPED)
    val parts = buildList {
        add(pluralStringResource(R.plurals.au_n_done, done, done))
        if (waiting > 0) add(pluralStringResource(R.plurals.au_n_waiting, waiting, waiting))
        if (failed > 0) add(pluralStringResource(R.plurals.au_n_failed, failed, failed))
        if (skipped > 0) add(pluralStringResource(R.plurals.au_n_skipped, skipped, skipped))
    }
    Text(parts.joinToString(" · ") + if (running && waiting > 0 && r.enabled) " · " + stringResource(R.string.au_running) else "",
        style = MaterialTheme.typography.bodySmall, color = if (failed > 0) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
    if (r.sinceMs > 0) Text(stringResource(R.string.au_since_short, Fmt.dateTime(ctx, r.sinceMs, false)), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}


/**
 * Ohne Rueckfrage loeschen: Android 11+ erlaubt das fuer fremde Fotos nur mit "Zugriff auf alle Dateien"
 * (so macht es auch die Nextcloud-App). Knopf fuehrt in die Android-Einstellung; zurueck in der App wird
 * Vorgemerktes gleich geloescht.
 */
@Composable
internal fun AllFilesAccessHint() {
    val ctx = LocalContext.current
    val owner = LocalLifecycleOwner.current
    var granted by remember { mutableStateOf(AutoUpload.allFilesAccess()) }
    LaunchedEffect(owner) {
        owner.lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            val now = AutoUpload.allFilesAccess()
            if (now && !granted) AutoUpload.deletePendingNow(ctx)
            granted = now
        }
    }
    if (granted || Build.VERSION.SDK_INT < 30) return
    Column(Modifier.padding(top = 8.dp)) {
        Text(stringResource(R.string.au_all_files_hint), style = MaterialTheme.typography.bodySmall)
        TextButton(onClick = {
            val i = Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.fromParts("package", ctx.packageName, null))
            try { ctx.startActivity(i) } catch (_: Exception) { ctx.startActivity(Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION)) }
        }) { Text(stringResource(R.string.au_all_files_grant)) }
    }
}

private const val WHICH_DATE = "date"
private const val WHICH_ALL = "all"
