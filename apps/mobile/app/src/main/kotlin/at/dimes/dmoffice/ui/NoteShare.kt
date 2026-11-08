package at.dimes.dmoffice.ui

import android.content.Context
import androidx.compose.foundation.clickable
import androidx.compose.material.icons.filled.Close
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.padding
import androidx.compose.ui.Alignment
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.NoteItem
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

/** Einzeln geteilte Notizen (Version 2): nur in dmOffice, nicht gecacht - wie Archiv und Papierkorb jedes Mal vom Server. */
object NoteShares {
    @Serializable data class Shared(val id: Long, val share: Int = 0, val title: String = "", val snippet: String = "", val color: String? = null,
                                    val modified: String = "", val owner: String = "", val canWrite: Boolean = false, val showOnly: Boolean = false,
                                    val image: String? = null, val attachments: Int = 0) {
        fun asItem() = NoteItem(id, title = title, snippet = snippet, color = color, modified = modified, created = modified, attachments = attachments)
    }

    /** Mit mir geteilter Ordner samt Unterordnern (der oberste ohne parent). */
    @Serializable data class SharedFolder(val syncId: String, val parent: String? = null, val name: String = "")
    @Serializable data class SharedRoot(val share: Int, val root: String, val owner: String = "", val name: String = "", val canWrite: Boolean = false,
                                        val showOnly: Boolean = false, val folders: List<SharedFolder> = emptyList())

    @Serializable data class Member(val id: String, val name: String = "", val readOnly: Boolean = false, val level: String? = null)
    @Serializable data class Person(val id: String, val login: String = "", val displayName: String = "")

    private fun api(ctx: Context) = App.get(ctx).api ?: throw IllegalStateException("Kein Konto")

    suspend fun withMe(ctx: Context): List<Shared> = withContext(Dispatchers.IO) { val a = api(ctx); a.get(a.url("/api/notes/single-shared")) }

    /** Mit mir geteilte Ordner (wie im Web in der Seitenleiste). */
    suspend fun folders(ctx: Context): List<SharedRoot> = withContext(Dispatchers.IO) { val a = api(ctx); a.get(a.url("/api/notes/shared")) }

    suspend fun leave(ctx: Context, share: Int) = withContext(Dispatchers.IO) { val a = api(ctx); a.postText(a.url("/api/notes/single-shared/$share/leave"), null) }

    suspend fun members(ctx: Context, note: Long): List<Member> = withContext(Dispatchers.IO) { val a = api(ctx); a.get(a.url("/api/notes/$note/shares")) }

    suspend fun people(ctx: Context): List<Person> = withContext(Dispatchers.IO) { val a = api(ctx); a.get(a.url("/api/pim/people")) }

    /** levels: Person -> show, send, share (fehlt = kein Zugriff). */
    suspend fun save(ctx: Context, note: Long, levels: Map<String, String>) = withContext(Dispatchers.IO) {
        val a = api(ctx)
        val body = buildJsonObject {
            put("members", buildJsonArray { levels.keys.forEach { add(kotlinx.serialization.json.JsonPrimitive(it)) } })
            put("readOnly", buildJsonArray { levels.filterValues { it != "share" }.keys.forEach { add(kotlinx.serialization.json.JsonPrimitive(it)) } })
            put("showOnly", buildJsonArray { levels.filterValues { it == "show" }.keys.forEach { add(kotlinx.serialization.json.JsonPrimitive(it)) } })
        }
        a.putText(a.url("/api/notes/$note/shares"), body.toString())
    }

    /** Freigabe-Nummer aus der Kennung einer einzeln geteilten Notiz: -(Freigabe * 2^32 + Id), Freigabe = 2^19 + Nummer. */
    fun shareOf(wire: Long): Int = ((-wire) shr 32).toInt() - (1 shl 19)
}

/** Eine eigene Notiz teilen: wer Zugriff hat (Zeigen, Senden oder Teilen, entfernen), darüber „Person hinzufügen“ mit Vorschlägen beim Tippen. */
@Composable
fun NoteShareDialog(note: Long, title: String, onDismiss: () -> Unit, onSaved: () -> Unit, onError: (String?) -> Unit) {
    val ctx = LocalContext.current
    PeopleShareDialog(title, stringResource(R.string.note_share_intro),
        load = { NoteShares.members(ctx, note).associate { it.id to (it.level ?: if (it.readOnly) "send" else "share") } },
        save = { NoteShares.save(ctx, note, it) }, onDismiss = onDismiss, onSaved = onSaved, onError = onError)
}

/**
 * Mit Personen teilen (Notizen, Dateien, Ordner): wer Zugriff hat (Zeigen, Senden oder Teilen, entfernen), darüber „Person hinzufügen“
 * mit Vorschlägen beim Tippen. load: bisherige Stufen je Person, save: die neuen.
 */
@Composable
fun PeopleShareDialog(title: String, intro: String, load: suspend () -> Map<String, String>, save: suspend (Map<String, String>) -> Unit,
                      onDismiss: () -> Unit, onSaved: () -> Unit, onError: (String?) -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var people by remember { mutableStateOf<List<NoteShares.Person>?>(null) }
    val levels = remember { mutableStateMapOf<String, String>() }
    LaunchedEffect(Unit) {
        try {
            levels.putAll(load())
            people = NoteShares.people(ctx)
        } catch (e: Exception) { onError(e.message); people = emptyList() }
    }
    val labels = listOf("show" to R.string.share_show, "send" to R.string.share_send, "share" to R.string.share_share)
    var query by remember { mutableStateOf("") }
    AlertDialog(onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.note_share_title, title), maxLines = 2, overflow = TextOverflow.Ellipsis) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(intro, style = MaterialTheme.typography.bodySmall)
                val list = people
                if (list == null) CircularProgressIndicator()
                else if (list.isEmpty()) Text(stringResource(R.string.share_nobody))
                else {
                    // Person hinzufügen: Vorschläge beim Tippen, neu hinzugefügt mit „Senden“
                    androidx.compose.material3.OutlinedTextField(query, { query = it }, Modifier.fillMaxWidth(), singleLine = true,
                        label = { Text(stringResource(R.string.share_add_person)) }, placeholder = { Text(stringResource(R.string.share_type_name)) })
                    val q = query.trim().lowercase()
                    if (q.isNotEmpty()) {
                        val matches = list.filter { it.id !in levels && (it.displayName.lowercase().contains(q) || it.login.lowercase().contains(q)) }
                        if (matches.isEmpty()) Text(stringResource(R.string.share_none_found), style = MaterialTheme.typography.bodySmall)
                        for (p in matches.take(6)) Text("${p.displayName} (${p.login})", Modifier.fillMaxWidth()
                            .clickable { levels[p.id] = "send"; query = "" }.padding(vertical = 8.dp), style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.primary)
                        if (matches.size > 6) Text(stringResource(R.string.share_people_more, matches.size - 6), style = MaterialTheme.typography.bodySmall)
                    }
                    Text(stringResource(R.string.share_with_access), style = MaterialTheme.typography.titleSmall)
                    val withAccess = list.filter { it.id in levels }
                    if (withAccess.isEmpty()) Text(stringResource(R.string.share_nobody_yet), style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                    else LazyColumn(Modifier.heightIn(max = 300.dp)) {
                        items(withAccess, key = { it.id }) { p ->
                            Column(Modifier.fillMaxWidth()) {
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Text("${p.displayName} (${p.login})", Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                    androidx.compose.material3.IconButton(onClick = { levels.remove(p.id) }) {
                                        androidx.compose.material3.Icon(androidx.compose.material.icons.Icons.Default.Close, contentDescription = stringResource(R.string.share_remove))
                                    }
                                }
                                Row(Modifier.horizontalScroll(androidx.compose.foundation.rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                                    for ((key, label) in labels) FilterChip(selected = levels[p.id] == key, onClick = { levels[p.id] = key }, label = { Text(stringResource(label)) })
                                }
                            }
                        }
                    }
                }
                Text(stringResource(R.string.share_letter_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        },
        confirmButton = {
            TextButton(enabled = people != null, onClick = {
                scope.launch { try { save(levels.toMap()); onSaved() } catch (e: Exception) { onError(e.message) } }
            }) { Text(stringResource(R.string.save)) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(android.R.string.cancel)) } })
}
