package at.dimes.dmoffice.ui

import android.content.Intent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.journal.Journal
import kotlinx.coroutines.launch
import java.io.File
import java.time.LocalDate

/**
 * Tagebuch exportieren: Tagebuch, Zeitraum (alles, dieses oder letztes Jahr), mit oder ohne Fotos und Videos. Die ZIP kommt
 * in den Zwischenspeicher der App und geht danach an „Teilen“ (Dateien-App, Cloud, Mail ...).
 */
@Composable
fun JournalExportDialog(onDismiss: () -> Unit, onError: (String) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var journals by remember { mutableStateOf<List<Journal>?>(null) }
    var target by remember { mutableStateOf<Long?>(null) }
    var range by remember { mutableIntStateOf(0) }
    var media by remember { mutableStateOf(true) }
    var busy by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        try {
            val l = app.journal.journals().filter { !it.showOnly }
            journals = l
            target = l.firstOrNull { it.owner == null }?.id ?: l.firstOrNull()?.id
        } catch (e: Exception) { onError(e.message ?: e.toString()); onDismiss() }
    }
    val year = LocalDate.now().year
    AlertDialog(onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(stringResource(R.string.journal_export)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                Text(stringResource(R.string.journal_export_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                val l = journals
                if (l == null) CircularProgressIndicator(Modifier.padding(16.dp))
                else {
                    if (l.size > 1) for (j in l) Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().clickable { target = j.id }) {
                        RadioButton(selected = target == j.id, onClick = { target = j.id })
                        Text(j.name + (j.owner?.let { " · $it" } ?: ""), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    HorizontalDivider(Modifier.padding(vertical = 6.dp))
                    listOf(stringResource(R.string.journal_export_all), year.toString(), (year - 1).toString()).forEachIndexed { i, label ->
                        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().clickable { range = i }) {
                            RadioButton(selected = range == i, onClick = { range = i })
                            Text(label)
                        }
                    }
                    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().padding(top = 6.dp)) {
                        Text(stringResource(R.string.journal_export_media), Modifier.weight(1f))
                        Switch(checked = media, onCheckedChange = { media = it })
                    }
                    if (busy) Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 8.dp)) {
                        CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                        Text(stringResource(R.string.journal_export_busy), Modifier.padding(start = 8.dp), style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        },
        confirmButton = {
            TextButton(enabled = !busy && target != null, onClick = {
                val id = target ?: return@TextButton
                val name = journals?.firstOrNull { it.id == id }?.name ?: "Tagebuch"
                val span: Pair<String?, String?> = when (range) {
                    1 -> "$year-01-01" to "$year-12-31"
                    2 -> "${year - 1}-01-01" to "${year - 1}-12-31"
                    else -> null to null
                }
                busy = true
                scope.launch {
                    try {
                        // Unter files/ im Zwischenspeicher - dort darf der FileProvider teilen; alte Exporte vorher weg
                        val dir = File(ctx.cacheDir, "files/export").apply { mkdirs(); listFiles()?.forEach { it.delete() } }
                        val safe = name.map { if (it in "\\/:*?\"<>|") '_' else it }.joinToString("")
                        val f = File(dir, "$safe ${LocalDate.now()}.zip")
                        app.journal.export(id, span.first, span.second, media, f)
                        val uri = FileProvider.getUriForFile(ctx, ctx.packageName + ".files", f)
                        val send = Intent(Intent.ACTION_SEND).setType("application/zip").putExtra(Intent.EXTRA_STREAM, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                        ctx.startActivity(Intent.createChooser(send, f.name))
                        onDismiss()
                    } catch (e: Exception) { onError(e.message ?: e.toString()); onDismiss() }
                    finally { busy = false }
                }
            }) { Text(stringResource(R.string.journal_export_go)) }
        },
        dismissButton = { TextButton(enabled = !busy, onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}
