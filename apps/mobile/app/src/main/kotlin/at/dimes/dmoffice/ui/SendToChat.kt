package at.dimes.dmoffice.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.chat.ChatOutRef
import at.dimes.dmoffice.chat.ChatRoom
import kotlinx.coroutines.launch

/**
 * „Im Chat …“ aus den Bereichen der App (Version 2): Unterhaltungen wählen, zeigen oder senden. Geschickt wird ein
 * Verweis, keine Kopie. Die Vorgabe kommt aus Einstellungen → Chat (Dateien bzw. Termine/Aufgaben/Kontakte).
 */
@Composable
fun SendToChatDialog(refs: List<ChatOutRef>, onDismiss: () -> Unit, onDone: (String?) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var rooms by remember { mutableStateOf<List<ChatRoom>?>(null) }
    var chosen by remember { mutableStateOf(setOf<Long>()) }
    val people = rememberPeopleWithoutChat()
    var chosenPeople by remember { mutableStateOf(setOf<String>()) }
    var mode by remember { mutableStateOf("show") }
    var busy by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        try {
            val s = app.chat.settings().settings
            mode = if (refs.all { it.type == "file" || it.type == "folder" || it.type == "note" }) s.filesMode else s.itemsMode
        } catch (_: Exception) { }
        rooms = try { app.chat.rooms() } catch (e: Exception) { onDone(e.message); emptyList() }
    }
    AlertDialog(onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.chat_send_to)) },
        text = {
            Column {
                val list = rooms
                if (list == null) CircularProgressIndicator()
                else if (list.isEmpty() && people.isEmpty()) Text(stringResource(R.string.chat_no_other_rooms))
                else LazyColumn(Modifier.heightIn(max = 300.dp)) {
                    items(list, key = { it.id }) { r ->
                        Row(Modifier.fillMaxWidth().clickable { chosen = if (r.id in chosen) chosen - r.id else chosen + r.id }, verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(checked = r.id in chosen, onCheckedChange = { chosen = if (it) chosen + r.id else chosen - r.id })
                            Text(r.title)
                        }
                    }
                    items(people, key = { "p" + it.id }) { p -> PersonRow(p, p.id in chosenPeople) { on -> chosenPeople = if (on) chosenPeople + p.id else chosenPeople - p.id } }
                }
                ModeChoice(mode) { mode = it }
            }
        },
        confirmButton = {
            TextButton(enabled = (chosen.isNotEmpty() || chosenPeople.isNotEmpty()) && !busy, onClick = {
                busy = true
                scope.launch {
                    try { app.chat.sendTo(chosen.toList() + chosenPeople.map { app.chat.open(listOf(it), null).id }, refs, mode); onDone(null) }
                    catch (e: Exception) { onDone(e.message ?: e.toString()) }
                    finally { busy = false }
                }
            }) { Text(stringResource(R.string.chat_send)) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(android.R.string.cancel)) } })
}
