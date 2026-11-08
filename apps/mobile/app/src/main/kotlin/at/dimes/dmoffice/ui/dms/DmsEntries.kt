package at.dimes.dmoffice.ui.dms

import android.widget.Toast
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ListItem
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
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import kotlinx.coroutines.launch

/** Ids des Eintrags und aller darunter. */
internal fun belowIds(all: List<DmsEntry>, id: String): Set<String> {
    val out = mutableSetOf(id)
    var grew = true
    while (grew) { grew = all.any { e -> e.parent != null && e.parent in out && out.add(e.id) } }
    return out
}

/**
 * Aktionen an einem Eintrag (langer Druck im Auswahldialog bzw. im Baum): „Neuer Untereintrag …“, „Umbenennen …“,
 * „Verschieben unter …“. onChanged bekommt die neu geladenen Listen.
 */
@Composable
internal fun EntryActions(entry: DmsEntry, entries: List<DmsEntry>, repo: DmsRepo, onDismiss: () -> Unit, onChanged: (List<DmsEntry>) -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var step by remember { mutableStateOf("menu") }
    var name by remember { mutableStateOf("") }
    fun run(block: suspend () -> Unit) = scope.launch {
        try { block(); onChanged(repo.lists()); onDismiss() }
        catch (e: Exception) { Toast.makeText(ctx, e.message ?: e.toString(), Toast.LENGTH_LONG).show() }
    }
    when (step) {
        "menu" -> AlertDialog(onDismissRequest = onDismiss, title = { Text(entry.name, maxLines = 2, overflow = TextOverflow.Ellipsis) },
            text = {
                Column {
                    ListItem(modifier = Modifier.clickable { name = ""; step = "child" }, headlineContent = { Text(stringResource(R.string.dms_entry_child)) })
                    ListItem(modifier = Modifier.clickable { name = entry.name; step = "rename" }, headlineContent = { Text(stringResource(R.string.dms_entry_rename)) })
                    ListItem(modifier = Modifier.clickable { step = "move" }, headlineContent = { Text(stringResource(R.string.dms_entry_move)) })
                }
            },
            confirmButton = {}, dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
        "child", "rename" -> AlertDialog(onDismissRequest = onDismiss,
            title = { Text(if (step == "child") stringResource(R.string.dms_entry_child_title, entry.name) else stringResource(R.string.dms_entry_rename)) },
            text = { OutlinedTextField(name, { name = it }, singleLine = true, label = { Text(stringResource(R.string.dms_entry_name)) }, modifier = Modifier.fillMaxWidth()) },
            confirmButton = {
                TextButton(enabled = name.isNotBlank(), onClick = {
                    val n = name.trim()
                    if (step == "child") run { repo.createEntry(entry.kind, n, entry.id) } else run { repo.updateEntry(entry.id, name = n) }
                }) { Text(stringResource(R.string.save)) }
            },
            dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
        else -> {
            // Verschieben unter: nicht unter sich selbst oder darunter
            val own = remember(entries) { belowIds(entries, entry.id) }
            val options = remember(entries) { ordered(entries, entry.kind).filter { it.first.id !in own } }
            AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.dms_entry_move_title, entry.name)) },
                text = {
                    LazyColumn(Modifier.fillMaxWidth().heightIn(max = 420.dp)) {
                        item {
                            ListItem(modifier = Modifier.clickable { run { repo.updateEntry(entry.id, parent = "") } },
                                headlineContent = { Text(stringResource(R.string.dms_entry_top), color = MaterialTheme.colorScheme.primary) })
                        }
                        items(options, key = { it.first.id }) { (e, depth) ->
                            ListItem(modifier = Modifier.clickable { run { repo.updateEntry(entry.id, parent = e.id) } },
                                headlineContent = { Text(e.name, Modifier.padding(start = (depth * 16).dp)) })
                        }
                    }
                },
                confirmButton = {}, dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
        }
    }
}
