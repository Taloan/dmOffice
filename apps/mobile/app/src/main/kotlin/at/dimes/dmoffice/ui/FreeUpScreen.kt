package at.dimes.dmoffice.ui

import android.app.Activity
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.outlined.CleaningServices
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import at.dimes.dmoffice.upload.AutoUpload
import at.dimes.dmoffice.upload.FreeSummary
import at.dimes.dmoffice.upload.FreeUpLogic
import at.dimes.dmoffice.upload.Scanner
import at.dimes.dmoffice.upload.UploadEntry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Hoechstens so viele Dateien in einem Systemdialog (die Liste der Uris geht durch den Binder). Mehr: der naechste folgt. */
private const val DELETE_REQUEST_MAX = 1500

/**
 * Karte oben in "Automatisch hochladen": wie viel Platz sich auf diesem Geraet freigeben laesst. Die Summe kommt
 * laufend aus der Upload-Datenbank (gesichert, noch da, unveraendert, aelter als die Schonfrist).
 */
@Composable
internal fun FreeUpCard(onOpen: () -> Unit) {
    val ctx = LocalContext.current
    val dao = remember { AutoUpload.dao(ctx) }
    val keep = AutoUpload.keepDays(ctx)
    val cutoff = remember(keep) { FreeUpLogic.cutoff(System.currentTimeMillis(), keep) }
    val sum by remember(cutoff) { dao.freeSummary(cutoff) }.collectAsState(initial = FreeSummary(0, 0))
    Card(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp).clickable(onClick = onOpen),
        colors = CardDefaults.cardColors(containerColor = if (sum.n > 0) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceVariant)) {
        Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Outlined.CleaningServices, contentDescription = null, modifier = Modifier.padding(end = 12.dp))
            Column(Modifier.weight(1f)) {
                if (sum.n > 0) {
                    Text(stringResource(R.string.au_free_card, FreeUpLogic.formatBytes(sum.bytes)), style = MaterialTheme.typography.titleSmall)
                    Text(pluralStringResource(R.plurals.au_free_count, sum.n, sum.n), style = MaterialTheme.typography.bodySmall)
                } else {
                    Text(stringResource(R.string.au_free_title), style = MaterialTheme.typography.titleSmall)
                    Text(stringResource(R.string.au_free_nothing), style = MaterialTheme.typography.bodySmall)
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FreeUpScreen(onClose: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val dao = remember { AutoUpload.dao(ctx) }
    var keep by remember { mutableIntStateOf(AutoUpload.keepDays(ctx)) }
    val cutoff = remember(keep) { FreeUpLogic.cutoff(System.currentTimeMillis(), keep) }
    val list by remember(cutoff) { dao.freeList(cutoff) }.collectAsState(initial = null)
    var busy by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf<String?>(null) }
    var confirm by remember { mutableStateOf(false) }
    // Was gerade im Systemdialog zum Loeschen liegt, und was danach noch folgt (mehr als ein Dialog)
    var inDialog by remember { mutableStateOf<List<UploadEntry>>(emptyList()) }
    var rest by remember { mutableStateOf<List<UploadEntry>>(emptyList()) }
    var keptTotal by remember { mutableIntStateOf(0) }
    var deletedTotal by remember { mutableIntStateOf(0) }

    fun result(): String = listOfNotNull(
        ctx.resources.getQuantityString(R.plurals.au_free_done, deletedTotal, deletedTotal),
        if (keptTotal > 0) ctx.resources.getQuantityString(R.plurals.au_free_kept, keptTotal, keptTotal) else null,
    ).joinToString(" ")

    lateinit var next: () -> Unit
    val deleteRequest = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) { res ->
        val batch = inDialog; inDialog = emptyList()
        scope.launch {
            // Abgelehnt: nichts als geloescht merken (bestaetigt wird ohnehin nur, was wirklich weg ist)
            val ok = res.resultCode == Activity.RESULT_OK
            if (ok) deletedTotal += AutoUpload.confirmDeleted(ctx, batch) else rest = emptyList()
            if (rest.isNotEmpty()) next()
            else { busy = false; message = if (!ok && deletedTotal == 0) ctx.getString(R.string.au_free_cancelled) else result() }
        }
    }
    next = {
        val batch = rest.take(DELETE_REQUEST_MAX); rest = rest.drop(DELETE_REQUEST_MAX)
        inDialog = batch
        val pi = MediaStore.createDeleteRequest(ctx.contentResolver, batch.map { Uri.parse(it.uri) })
        deleteRequest.launch(IntentSenderRequest.Builder(pi.intentSender).build())
    }

    fun freeUp() {
        busy = true; message = null; keptTotal = 0; deletedTotal = 0
        scope.launch {
            try {
                // Direkt vor dem Loeschen: auf dem Geraet unveraendert? Und hat der Server genau diesen Inhalt?
                val plan = AutoUpload.planFreeUp(ctx, dao.freeListNow(cutoff))
                keptTotal = plan.kept
                if (plan.delete.isEmpty()) { busy = false; message = result(); return@launch }
                if (Build.VERSION.SDK_INT >= 30 && !AutoUpload.allFilesAccess()) { rest = plan.delete; next() }
                else { deletedTotal = AutoUpload.deleteDirect(ctx, plan.delete); busy = false; message = result() }
            } catch (e: Exception) {
                busy = false
                message = ctx.getString(R.string.au_free_failed, e.message ?: e.toString())
            }
        }
    }

    BackHandler { onClose() }
    Scaffold(topBar = {
        TopAppBar(title = { Text(stringResource(R.string.au_free_title)) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } })
    }) { pad ->
        val items = list ?: emptyList()
        val bytes = items.sumOf { it.size }
        LazyColumn(Modifier.padding(pad).fillMaxSize()) {
            item(key = "head") {
                Column(Modifier.padding(16.dp)) {
                    Text(if (items.isEmpty()) stringResource(R.string.au_free_nothing) else stringResource(R.string.au_free_card, FreeUpLogic.formatBytes(bytes)),
                        style = MaterialTheme.typography.titleMedium)
                    if (items.isNotEmpty()) Text(pluralStringResource(R.plurals.au_free_count, items.size, items.size), style = MaterialTheme.typography.bodyMedium)
                    Text(stringResource(R.string.au_free_explain), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(top = 8.dp))
                    Text(stringResource(R.string.au_free_keep), style = MaterialTheme.typography.labelLarge, modifier = Modifier.padding(top = 12.dp))
                    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        for (d in FreeUpLogic.KEEP_CHOICES) FilterChip(selected = keep == d, enabled = !busy,
                            onClick = { keep = d; AutoUpload.setKeepDays(ctx, d) },
                            label = { Text(if (d == 0) stringResource(R.string.au_free_keep_none) else pluralStringResource(R.plurals.au_free_keep_days, d, d)) })
                    }
                    Row(Modifier.fillMaxWidth().padding(top = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                        Button(onClick = { if (AutoUpload.allFilesAccess()) confirm = true else freeUp() }, enabled = !busy && items.isNotEmpty()) {
                            Text(stringResource(R.string.au_free_button, FreeUpLogic.formatBytes(bytes)))
                        }
                        if (busy) {
                            CircularProgressIndicator(Modifier.padding(start = 12.dp).size(20.dp), strokeWidth = 2.dp)
                            Text(stringResource(R.string.au_free_checking), style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(start = 8.dp))
                        }
                    }
                    message?.let { Text(it, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(top = 8.dp), color = MaterialTheme.colorScheme.primary) }
                    AllFilesAccessHint()
                }
                HorizontalDivider()
            }
            items(items, key = { it.id }) { e -> FreeRow(e); HorizontalDivider() }
        }
    }

    if (confirm) AlertDialog(onDismissRequest = { confirm = false },
        title = { Text(stringResource(R.string.au_free_confirm_title, FreeUpLogic.formatBytes(list.orEmpty().sumOf { it.size }))) },
        text = { Text(pluralStringResource(R.plurals.au_free_confirm, list.orEmpty().size, list.orEmpty().size)) },
        confirmButton = { Button(onClick = { confirm = false; freeUp() }) { Text(stringResource(R.string.au_free_delete)) } },
        dismissButton = { TextButton(onClick = { confirm = false }) { Text(stringResource(R.string.cancel)) } })
}

/** Eine Datei der Liste: Vorschau, Name, Groesse, Aufnahmedatum, wo sie in dmOffice liegt. */
@Composable
private fun FreeRow(e: UploadEntry) {
    val ctx = LocalContext.current
    val thumb by produceState<android.graphics.Bitmap?>(null, e.uri) {
        value = withContext(Dispatchers.IO) { Scanner.thumbnail(ctx, Uri.parse(e.uri)) }
    }
    ListItem(
        leadingContent = {
            val b = thumb
            if (b != null) Image(b.asImageBitmap(), contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.size(48.dp).clip(RoundedCornerShape(6.dp)))
            else Box(Modifier.size(48.dp).clip(RoundedCornerShape(6.dp)).background(MaterialTheme.colorScheme.surfaceVariant))
        },
        headlineContent = { Text(e.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        supportingContent = {
            Column {
                Text(FreeUpLogic.formatBytes(e.size) + " · " + Fmt.dateTime(ctx, if (e.taken > 0) e.taken else e.modified, false),
                    style = MaterialTheme.typography.bodySmall)
                e.remotePath?.let { Text(stringResource(R.string.au_free_where, it), style = MaterialTheme.typography.bodySmall, maxLines = 1,
                    overflow = TextOverflow.Ellipsis, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            }
        })
}
