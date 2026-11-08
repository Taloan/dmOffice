package at.dimes.dmoffice.ui

import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.OpenableColumns
import android.text.format.Formatter
import android.widget.Toast
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.InsertDriveFile
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.outlined.AutoStories
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Image
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.fragment.app.FragmentActivity
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.upload.ShareUploadWorker
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import java.io.File

/**
 * Teilen an dmOffice (Version 2): Dateien aus jeder App - in einen Ordner der Dateiablage (wie bei Nextcloud: Ordner wählen,
 * der zuletzt gewählte ist vorgeschlagen) oder Fotos und Videos ins Tagebuch (je Aufnahmetag ein Eintrag mit Datum und Ort
 * aus dem Bild) oder im DMS (Ablageprofil wählbar). Hochgeladen wird im Hintergrund, nach DMS direkt; die App-Sperre gilt
 * auch hier.
 */
class ShareActivity : FragmentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val app = App.get(this)
        val uris = shared(intent)
        if (app.prefs.account.value == null) { Toast.makeText(this, R.string.share_no_account, Toast.LENGTH_LONG).show(); finish(); return }
        if (uris.isEmpty()) { Toast.makeText(this, R.string.share_nothing, Toast.LENGTH_LONG).show(); finish(); return }
        setContent {
            AppTheme {
                val settings by app.prefs.settings.collectAsState()
                var unlocked by remember { mutableStateOf(false) }
                if (settings.appLock && !unlocked) { LockScreen(this) { unlocked = true }; return@AppTheme }
                ShareScreen(uris) { finish() }
            }
        }
    }

    @Suppress("DEPRECATION")
    private fun shared(intent: Intent): List<Uri> {
        val list = when (intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(
                if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java) else intent.getParcelableExtra(Intent.EXTRA_STREAM))
            Intent.ACTION_SEND_MULTIPLE ->
                (if (Build.VERSION.SDK_INT >= 33) intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java) else intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM)) ?: emptyList()
            else -> emptyList()
        }
        // Manche Apps legen die Dateien nur in ClipData ab
        val clip = intent.clipData?.let { c -> (0 until c.itemCount).mapNotNull { c.getItemAt(it).uri } } ?: emptyList()
        return (list + clip).distinct()
    }
}

private data class Shared(val uri: Uri, val name: String, val size: Long, val mime: String)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ShareScreen(uris: List<Uri>, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val prefs = remember { ctx.getSharedPreferences("share", android.content.Context.MODE_PRIVATE) }
    var folder by remember { mutableStateOf(prefs.getString("folder", "") ?: "") }
    var picking by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var items by remember { mutableStateOf<List<Shared>?>(null) }
    var journals by remember { mutableStateOf<List<Pair<Long, String>>>(emptyList()) }
    var journal by remember { mutableStateOf<Long?>(null) }
    var dmsProfiles by remember { mutableStateOf<List<at.dimes.dmoffice.ui.dms.DmsProfile>>(emptyList()) }
    var pickDms by remember { mutableStateOf(false) }

    LaunchedEffect(uris) {
        items = withContext(Dispatchers.IO) {
            uris.map { u ->
                var name: String? = null
                var size = -1L
                try {
                    ctx.contentResolver.query(u, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
                        if (c.moveToFirst()) { if (!c.isNull(0)) name = c.getString(0); if (!c.isNull(1)) size = c.getLong(1) }
                    }
                } catch (_: Exception) { }
                Shared(u, name?.takeIf { it.isNotBlank() } ?: (u.lastPathSegment ?: "Datei"), size,
                    ctx.contentResolver.getType(u) ?: at.dimes.dmoffice.files.FilesRepo.guessMime(name ?: ""))
            }
        }
        // Tagebücher (Server ab Version 2) - sonst gibt es nur die Dateiablage
        journals = try {
            withContext(Dispatchers.IO) {
                val api = app.api ?: return@withContext emptyList()
                (api.json.parseToJsonElement(api.getText(api.url("/api/journal/journals"))) as JsonArray).map {
                    val o = it.jsonObject
                    o["id"]!!.jsonPrimitive.long to o["name"]!!.jsonPrimitive.content
                }
            }
        } catch (_: Exception) { emptyList() }
        journal = journals.firstOrNull()?.first
        if (at.dimes.dmoffice.ui.dms.dmsOn()) dmsProfiles = try { at.dimes.dmoffice.ui.dms.DmsRepo(ctx).profiles() } catch (_: Exception) { emptyList() }
    }

    /** Im DMS: kopieren und gleich hochladen (Texterkennung und Ablage macht der Server). */
    fun toDms(profile: String?) {
        val list = items ?: return
        busy = true
        scope.launch {
            try {
                val repo = at.dimes.dmoffice.ui.dms.DmsRepo(ctx)
                var dup = 0
                for (s in list) {
                    val (f, name, mime) = withContext(Dispatchers.IO) { at.dimes.dmoffice.ui.dms.copyToCache(ctx, s.uri) } ?: continue
                    try { if (repo.upload(f, s.name, mime ?: s.mime, profile, tresor = false).duplicate) dup++ } finally { f.delete() }
                }
                Toast.makeText(ctx, if (dup > 0) ctx.getString(R.string.dms_duplicates, dup)
                    else ctx.resources.getQuantityString(R.plurals.dms_uploaded, list.size, list.size), Toast.LENGTH_SHORT).show()
                onClose()
            } catch (e: Exception) {
                busy = false
                Toast.makeText(ctx, e.message ?: e.toString(), Toast.LENGTH_LONG).show()
            }
        }
    }

    /** In den Cache kopieren (die Leserechte gelten nur kurz) und den Auftrag im Hintergrund starten. */
    fun send(toJournal: Boolean) {
        val list = items ?: return
        busy = true
        scope.launch {
            try {
                val batch = System.currentTimeMillis().toString()
                withContext(Dispatchers.IO) {
                    val dir = ShareUploadWorker.batchDir(ctx, batch).apply { mkdirs() }
                    for (s in list) {
                        if (toJournal && !(s.mime.startsWith("image/") || s.mime.startsWith("video/"))) continue
                        var target = File(dir, s.name.replace('/', '_'))
                        var n = 2
                        while (target.exists()) target = File(dir, s.name.substringBeforeLast('.') + " ($n)" + (if (s.name.contains('.')) "." + s.name.substringAfterLast('.') else "")).also { n++ }
                        ctx.contentResolver.openInputStream(s.uri)?.use { i -> target.outputStream().use { o -> i.copyTo(o) } }
                    }
                }
                if (toJournal) {
                    // Ins Tagebuch: der Dialog für einen neuen Eintrag öffnet sich mit den Fotos, Datum und Ort aus dem ersten
                    ctx.startActivity(android.content.Intent(ctx, at.dimes.dmoffice.MainActivity::class.java)
                        .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK or android.content.Intent.FLAG_ACTIVITY_CLEAR_TOP)
                        .putExtra(at.dimes.dmoffice.MainActivity.EXTRA_ROUTE, "journal/new?share=$batch&journal=${journal ?: -1}"))
                    onClose()
                    return@launch
                }
                prefs.edit().putString("folder", folder).apply(); ShareUploadWorker.start(ctx, batch, folder, null)
                Toast.makeText(ctx, R.string.share_started, Toast.LENGTH_SHORT).show()
                onClose()
            } catch (e: Exception) {
                busy = false
                Toast.makeText(ctx, e.message ?: e.toString(), Toast.LENGTH_LONG).show()
            }
        }
    }

    Scaffold(topBar = {
        TopAppBar(title = { Text(stringResource(R.string.share_title)) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } })
    }) { pad ->
        val list = items
        if (list == null || busy) {
            Column(Modifier.fillMaxSize().padding(pad), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
                CircularProgressIndicator()
                if (busy) Text(stringResource(R.string.share_preparing), Modifier.padding(top = 12.dp))
            }
            return@Scaffold
        }
        val media = list.count { it.mime.startsWith("image/") || it.mime.startsWith("video/") }
        Column(Modifier.fillMaxSize().padding(pad).verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            val total = list.sumOf { it.size.coerceAtLeast(0) }
            Text(ctx.resources.getQuantityString(R.plurals.share_files, list.size, list.size) + if (total > 0) " · " + Formatter.formatShortFileSize(ctx, total) else "",
                style = MaterialTheme.typography.titleMedium)
            Card(Modifier.fillMaxWidth()) {
                list.take(6).forEach { s ->
                    ListItem(headlineContent = { Text(s.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                        leadingContent = { Icon(if (s.mime.startsWith("image/")) Icons.Outlined.Image else Icons.AutoMirrored.Outlined.InsertDriveFile, contentDescription = null) },
                        supportingContent = if (s.size >= 0) { { Text(Formatter.formatShortFileSize(ctx, s.size)) } } else null)
                }
                if (list.size > 6) Text(stringResource(R.string.share_more, list.size - 6), Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
            }

            // In die Dateiablage
            Text(stringResource(R.string.share_to_files), style = MaterialTheme.typography.titleSmall)
            Card(Modifier.fillMaxWidth()) {
                ListItem(headlineContent = { Text(folder.ifEmpty { stringResource(R.string.files) }, maxLines = 2, overflow = TextOverflow.Ellipsis) },
                    supportingContent = { Text(stringResource(R.string.share_folder)) },
                    leadingContent = { Icon(Icons.Outlined.Folder, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                    trailingContent = { TextButton(onClick = { picking = true }) { Text(stringResource(R.string.share_change)) } })
                Button(onClick = { send(false) }, modifier = Modifier.fillMaxWidth().padding(16.dp)) { Text(stringResource(R.string.share_upload)) }
            }

            // Im DMS
            if (dmsProfiles.isNotEmpty()) {
                Text(stringResource(R.string.share_to_dms), style = MaterialTheme.typography.titleSmall)
                Card(Modifier.fillMaxWidth()) {
                    Text(stringResource(R.string.share_dms_hint), Modifier.padding(16.dp), style = MaterialTheme.typography.bodyMedium)
                    Button(onClick = { if (dmsProfiles.size > 1) pickDms = true else toDms(dmsProfiles.first().id) },
                        modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 16.dp, bottom = 16.dp)) { Text(stringResource(R.string.share_dms_button)) }
                }
            }

            // Ins Tagebuch
            if (media > 0 && journals.isNotEmpty()) {
                Text(stringResource(R.string.share_to_journal), style = MaterialTheme.typography.titleSmall)
                Card(Modifier.fillMaxWidth()) {
                    Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Outlined.AutoStories, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                        Text(stringResource(R.string.share_journal_hint), Modifier.padding(start = 12.dp), style = MaterialTheme.typography.bodyMedium)
                    }
                    if (media < list.size) Text(stringResource(R.string.share_journal_only_media), Modifier.padding(horizontal = 16.dp),
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    if (journals.size > 1) journals.forEach { (id, name) ->
                        Row(Modifier.fillMaxWidth().selectable(selected = journal == id, role = Role.RadioButton) { journal = id }.padding(horizontal = 8.dp),
                            verticalAlignment = Alignment.CenterVertically) {
                            RadioButton(selected = journal == id, onClick = null, modifier = Modifier.padding(8.dp))
                            Text(name)
                        }
                    }
                    Button(onClick = { send(true) }, enabled = journal != null, modifier = Modifier.fillMaxWidth().padding(16.dp)) { Text(stringResource(R.string.share_journal_button)) }
                }
            }
        }
    }

    if (pickDms) at.dimes.dmoffice.ui.dms.ProfileDialog(dmsProfiles, onDismiss = { pickDms = false }) { p -> pickDms = false; toDms(p.id) }
    if (picking) FolderPickerDialog(start = folder, exclude = null, onDismiss = { picking = false },
        title = stringResource(R.string.share_pick_folder), confirm = stringResource(R.string.share_here)) { folder = it; picking = false }
}
