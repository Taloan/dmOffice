package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.InputChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.PhotoInfoBody
import at.dimes.dmoffice.files.PhotoMeta
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private val Writable = Regex("""\.(jpe?g|arw|cr2|cr3|nef|nrw|orf|rw2|raf|pef|dng|srw|3fr|erf)$""", RegexOption.IGNORE_CASE)

/** Lassen sich Titel, Bildunterschrift, Bewertung und Stichwörter schreiben? JPEG (in die Datei) und RAW (XMP-Datei daneben). */
fun photoInfoWritable(items: List<FileItem>) = items.isNotEmpty() && items.all { !it.isDir && Writable.containsMatchIn(it.name) }

/**
 * Titel, Bildunterschrift, Bewertung und Stichwörter eines oder mehrerer Fotos ändern (wie in der Web-Galerie). Bei mehreren bleibt
 * jedes Feld, das man nicht anfasst; Stichwörter kommen bei allen dazu bzw. gehen bei allen weg.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun PhotoInfoDialog(items: List<FileItem>, metas: Map<String, PhotoMeta>, onDismiss: () -> Unit, onDone: () -> Unit, onError: (String) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val ms = items.map { metas[it.path] }
    fun <T> common(values: List<T>): T? = values.firstOrNull()?.takeIf { f -> values.all { it == f } }
    val startTitle = common(ms.map { it?.title ?: "" })
    val startDesc = common(ms.map { it?.description ?: "" })
    val startRating = common(ms.map { it?.rating ?: 0 })
    var title by remember { mutableStateOf(startTitle ?: "") }
    var desc by remember { mutableStateOf(startDesc ?: "") }
    var rating by remember { mutableStateOf(startRating ?: 0) }
    var touched by remember { mutableStateOf(setOf<String>()) }
    // Stichwörter: je Name, wie viele der Fotos es haben
    val counts = remember(items) {
        val m = linkedMapOf<String, Pair<String, Int>>()
        for (x in ms) for (k in (x?.keywords ?: "").split('\n').map { it.trim() }.filter { it.isNotEmpty() }) {
            val c = m[k.lowercase()]; m[k.lowercase()] = (c?.first ?: k) to ((c?.second ?: 0) + 1)
        }
        m
    }
    var removed by remember { mutableStateOf(listOf<String>()) }
    var added by remember { mutableStateOf(listOf<String>()) }
    var kw by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var failed by remember { mutableStateOf(listOf<String>()) }
    fun addKw() {
        val parts = kw.split(',', ';').map { it.trim() }.filter { it.isNotEmpty() }
        added = added + parts.filter { p -> added.none { it.equals(p, true) } }
        removed = removed.filter { r -> parts.none { it.equals(r, true) } }
        kw = ""
    }
    val mixed = stringResource(R.string.photo_info_mixed)
    val any = touched.isNotEmpty() || added.isNotEmpty() || removed.isNotEmpty() || kw.isNotBlank()

    AlertDialog(onDismissRequest = { if (!busy) onDismiss() },
        title = { Text(items.singleOrNull()?.name ?: stringResource(R.string.photo_info_n, items.size)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedTextField(title, { title = it; touched = touched + "title" }, Modifier.fillMaxWidth(), singleLine = true, enabled = !busy,
                    label = { Text(stringResource(R.string.photo_info_title)) }, placeholder = if (startTitle == null) { { Text(mixed) } } else null)
                OutlinedTextField(desc, { desc = it; touched = touched + "desc" }, Modifier.fillMaxWidth(), minLines = 2, enabled = !busy,
                    label = { Text(stringResource(R.string.photo_info_caption)) }, placeholder = if (startDesc == null) { { Text(mixed) } } else null)
                Column {
                    Text(stringResource(R.string.photo_info_rating), style = MaterialTheme.typography.labelLarge)
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        for (n in 1..5) IconButton(onClick = { rating = if (rating == n) 0 else n; touched = touched + "rating" }, enabled = !busy, modifier = Modifier.size(40.dp)) {
                            Icon(if (n <= rating) Icons.Default.Star else Icons.Default.StarBorder, contentDescription = "$n", tint = androidx.compose.ui.graphics.Color(0xFFE8A33D))
                        }
                    }
                    if (startRating == null && "rating" !in touched) Text(stringResource(R.string.photo_info_rating_mixed), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(stringResource(R.string.photo_info_keywords), style = MaterialTheme.typography.labelLarge)
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        for ((_, c) in counts) if (removed.none { it.equals(c.first, true) })
                            InputChip(selected = false, enabled = !busy, onClick = { removed = removed + c.first },
                                label = { Text(if (c.second < items.size) "${c.first} (${c.second}/${items.size})" else c.first) },
                                trailingIcon = { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.photo_info_remove), Modifier.size(16.dp)) })
                        for (k in added) InputChip(selected = true, enabled = !busy, onClick = { added = added - k }, label = { Text(k) },
                            trailingIcon = { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.photo_info_remove), Modifier.size(16.dp)) })
                    }
                    OutlinedTextField(kw, { kw = it }, Modifier.fillMaxWidth(), singleLine = true, enabled = !busy,
                        placeholder = { Text(stringResource(R.string.photo_info_keyword_hint)) },
                        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done), keyboardActions = KeyboardActions(onDone = { addKw() }),
                        trailingIcon = { IconButton(onClick = { addKw() }, enabled = kw.isNotBlank()) { Icon(Icons.Default.Add, contentDescription = stringResource(R.string.photo_info_add)) } })
                    if (items.size > 1) Text(stringResource(R.string.photo_info_keywords_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Text(stringResource(R.string.photo_info_where), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (failed.isNotEmpty()) Text(failed.joinToString("\n"), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = {
            TextButton(enabled = any && !busy, onClick = {
                val body = PhotoInfoBody(items = emptyList(),
                    title = if ("title" in touched) title else null, description = if ("desc" in touched) desc else null,
                    rating = if ("rating" in touched) rating else null,
                    addKeywords = added + kw.split(',', ';').map { it.trim() }.filter { it.isNotEmpty() }, removeKeywords = removed)
                busy = true
                scope.launch {
                    val bad = mutableListOf<String>()
                    try {
                        withContext(Dispatchers.IO) {
                            for (chunk in items.chunked(50)) bad += app.files.photoInfo(body.copy(items = chunk.map { it.path })).map { "${it.name}: ${it.error}" }
                        }
                    } catch (e: Exception) { onError(e.message ?: e.toString()) }
                    busy = false
                    onDone()
                    if (bad.isEmpty()) onDismiss() else failed = bad
                }
            }) { Text(stringResource(R.string.save)) }
        },
        dismissButton = { TextButton(onClick = onDismiss, enabled = !busy) { Text(stringResource(if (failed.isEmpty()) R.string.cancel else R.string.close)) } })
}
