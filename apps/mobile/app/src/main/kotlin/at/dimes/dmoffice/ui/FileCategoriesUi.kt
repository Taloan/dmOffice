package at.dimes.dmoffice.ui

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TriStateCheckbox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.state.ToggleableState
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.files.CatChange
import at.dimes.dmoffice.files.CatTree
import at.dimes.dmoffice.files.FileCat
import at.dimes.dmoffice.files.FileLabels
import at.dimes.dmoffice.files.LabelsSet
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Farben neuer Kategorien (wie im Web). */
private val CAT_PALETTE = listOf("#D13438", "#CA5010", "#C19C00", "#13A10E", "#00B7C3", "#0F6CBD", "#8764B8", "#E3008C", "#8A8886", "#4F6BED")

fun catColor(hex: String?, fallback: Color): Color =
    try { if (hex.isNullOrBlank()) fallback else Color(android.graphics.Color.parseColor(hex)) } catch (_: Exception) { fallback }

@Composable
fun CatDot(hex: String?, size: Dp = 10.dp) {
    Box(Modifier.size(size).clip(CircleShape).background(catColor(hex, MaterialTheme.colorScheme.outline)))
}

/** Baum in Lesereihenfolge mit Tiefe. */
private fun flat(tree: CatTree): List<Pair<FileCat, Int>> {
    val out = mutableListOf<Pair<FileCat, Int>>()
    fun walk(parent: String?, depth: Int) { for (c in tree.kids(parent)) { out.add(c to depth); walk(c.id, depth + 1) } }
    walk(null, 0)
    return out
}

/** Weg im Bereich Kategorien: „Kategorien › Finanzen › Steuer“ bzw. „Kategorien › #Tag“; antippen geht dorthin. */
@Composable
fun CategoryCrumb(tree: CatTree?, cat: String?, tag: String?, onGo: (String?) -> Unit) {
    Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        TextButton(onClick = { onGo(null) }) { Text(stringResource(R.string.files_area_categories)) }
        if (tag != null) { Text("›"); TextButton(onClick = {}) { Text("#$tag") } }
        if (cat != null && tree != null) {
            for (id in tree.above(cat).reversed() + cat) {
                Text("›")
                TextButton(onClick = { onGo(id) }) { Text(tree.cats.firstOrNull { it.id == id }?.name ?: "?") }
            }
        }
    }
}

/** Unterkategorien der geöffneten Kategorie als Chips. */
@Composable
fun SubCategoryChips(tree: CatTree?, cat: String, onOpen: (String) -> Unit) {
    val kids = tree?.kids(cat) ?: emptyList()
    if (kids.isEmpty()) return
    LazyRow(contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        items(kids, key = { it.id }) { c ->
            FilterChip(selected = false, onClick = { onOpen(c.id) }, label = { Text(c.name + (tree!!.total(c.id).takeIf { it > 0 }?.let { " · $it" } ?: "")) },
                leadingIcon = { CatDot(c.color) })
        }
    }
}

/**
 * Oberste Ebene: alle Kategorien als Baum (antippen öffnet den virtuellen Ordner, lange tippen verwaltet), dazu die Tags.
 * Ohne Kategorien: Erklärung und Vorschlag zum Übernehmen.
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun CategoryRoot(tree: CatTree?, onOpen: (String) -> Unit, onTag: (String) -> Unit, onChanged: () -> Unit, onError: (String) -> Unit) {
    val repo = App.get(LocalContext.current).files
    val scope = rememberCoroutineScope()
    var manage by remember { mutableStateOf<FileCat?>(null) }
    var naming by remember { mutableStateOf<Pair<String?, FileCat?>?>(null) }  // (Oberkategorie, umzubenennende)
    fun run(block: () -> Unit) = scope.launch {
        try { withContext(Dispatchers.IO) { block() } } catch (e: Exception) { onError(e.message ?: e.toString()) }
        onChanged()
    }
    if (tree == null) { Box(Modifier.fillMaxSize()) { CircularProgressIndicator(Modifier.align(Alignment.Center)) }; return }
    LazyColumn(Modifier.fillMaxSize()) {
        if (tree.cats.isEmpty()) item(key = "empty") {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.cats_empty), style = MaterialTheme.typography.bodyMedium)
                Button(onClick = { run { repo.categorySuggestion() } }) { Text(stringResource(R.string.cats_suggest)) }
                Text(stringResource(R.string.cats_suggest_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        items(flat(tree), key = { it.first.id }) { (c, depth) ->
            val pending = tree.total(c.id, unconfirmed = true)
            ListItem(
                modifier = Modifier.combinedClickable(onClick = { onOpen(c.id) }, onLongClick = { manage = c }),
                leadingContent = { Row(verticalAlignment = Alignment.CenterVertically) { Spacer(Modifier.width((depth * 18).dp)); CatDot(c.color, 12.dp) } },
                headlineContent = { Text(c.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                supportingContent = if (pending > 0) ({ Text(stringResource(R.string.cats_unconfirmed, pending), color = MaterialTheme.colorScheme.primary) }) else null,
                trailingContent = { tree.total(c.id).takeIf { it > 0 }?.let { Text(it.toString(), color = MaterialTheme.colorScheme.onSurfaceVariant) } })
        }
        item(key = "new") {
            TextButton(onClick = { naming = null to null }, modifier = Modifier.padding(horizontal = 8.dp)) {
                Icon(Icons.Default.Add, contentDescription = null); Spacer(Modifier.width(6.dp)); Text(stringResource(R.string.cats_new))
            }
        }
        if (tree.tags.isNotEmpty()) {
            item(key = "tagshead") {
                HorizontalDivider()
                Text(stringResource(R.string.cats_tags), style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.padding(start = 16.dp, top = 12.dp, bottom = 4.dp))
            }
            items(tree.tags, key = { "t:" + it.tag }) { g ->
                ListItem(modifier = Modifier.combinedClickable(onClick = { onTag(g.tag) }),
                    headlineContent = { Text("#" + g.tag) }, trailingContent = { Text(g.count.toString(), color = MaterialTheme.colorScheme.onSurfaceVariant) })
            }
        }
    }
    manage?.let { c ->
        var confirmDelete by remember(c.id) { mutableStateOf(false) }
        if (confirmDelete) AlertDialog(onDismissRequest = { manage = null },
            text = { Text(stringResource(R.string.cats_delete_ask, c.name)) },
            confirmButton = { TextButton(onClick = { manage = null; run { repo.deleteCategory(c.id) } }) { Text(stringResource(R.string.delete), color = MaterialTheme.colorScheme.error) } },
            dismissButton = { TextButton(onClick = { manage = null }) { Text(stringResource(R.string.cancel)) } })
        else AlertDialog(onDismissRequest = { manage = null },
            title = { Text(tree.fullName(c.id)) },
            text = {
                Column {
                    TextButton(onClick = { manage = null; naming = c.id to null }) { Text(stringResource(R.string.cats_new_sub)) }
                    TextButton(onClick = { manage = null; naming = c.parent to c }) { Text(stringResource(R.string.cats_rename)) }
                    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp)) {
                        Text(stringResource(R.string.cats_learn), Modifier.weight(1f))
                        Switch(checked = c.learn, onCheckedChange = { v -> manage = c.copy(learn = v, auto = c.auto && v); run { repo.changeCategory(c.id, CatChange(learn = v)) } })
                    }
                    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp)) {
                        Text(stringResource(R.string.cats_auto), Modifier.weight(1f))
                        Switch(checked = c.auto, enabled = c.learn, onCheckedChange = { v -> manage = c.copy(auto = v); run { repo.changeCategory(c.id, CatChange(auto = v)) } })
                    }
                    TextButton(onClick = { confirmDelete = true }) { Text(stringResource(R.string.delete), color = MaterialTheme.colorScheme.error) }
                }
            },
            confirmButton = { TextButton(onClick = { manage = null }) { Text(stringResource(R.string.close)) } })
    }
    naming?.let { (parent, renaming) ->
        NameDialog(initial = renaming?.name ?: "", onDismiss = { naming = null }) { name ->
            naming = null
            run {
                if (renaming != null) repo.changeCategory(renaming.id, CatChange(name = name))
                else repo.createCategory(name, parent, parent?.let { p -> tree.cats.firstOrNull { it.id == p }?.color } ?: CAT_PALETTE[tree.cats.size % CAT_PALETTE.size])
            }
        }
    }
}

@Composable
private fun NameDialog(initial: String, onDismiss: () -> Unit, onOk: (String) -> Unit) {
    var name by remember { mutableStateOf(initial) }
    AlertDialog(onDismissRequest = onDismiss,
        text = { OutlinedTextField(value = name, onValueChange = { name = it }, singleLine = true, label = { Text(stringResource(R.string.cats_name)) }) },
        confirmButton = { TextButton(enabled = name.isNotBlank(), onClick = { onOk(name.trim()) }) { Text(stringResource(R.string.save)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

/** Im virtuellen Ordner: automatisch Eingeordnetes - alle bestätigen oder nur diese zeigen. */
@Composable
fun UnconfirmedBar(count: Int, onlyThese: Boolean, onToggle: () -> Unit, onConfirmAll: () -> Unit) {
    if (count == 0) return
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(Icons.Default.AutoAwesome, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(6.dp))
        Text(stringResource(R.string.cats_unconfirmed, count), style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
        TextButton(onClick = onToggle) { Text(stringResource(if (onlyThese) R.string.cats_show_all else R.string.cats_only_auto)) }
        TextButton(onClick = onConfirmAll) { Text(stringResource(R.string.cats_confirm_all)) }
    }
}

/** Kategorien und Tags für ausgewählte Dateien setzen. Bei mehreren: halb angehakt = bei einigen. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun LabelsDialog(paths: List<String>, onDismiss: () -> Unit, onDone: () -> Unit, onError: (String) -> Unit) {
    val repo = App.get(LocalContext.current).files
    val scope = rememberCoroutineScope()
    var tree by remember { mutableStateOf<CatTree?>(null) }
    var of by remember { mutableStateOf<Map<String, FileLabels>?>(null) }
    var cat by remember { mutableStateOf<Map<String, Boolean>>(emptyMap()) }
    var tags by remember { mutableStateOf<Map<String, Boolean>>(emptyMap()) }
    var newTag by remember { mutableStateOf("") }
    var newCat by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    suspend fun loadTree() { tree = withContext(Dispatchers.IO) { repo.categories() } }
    LaunchedEffect(Unit) {
        try { loadTree(); of = withContext(Dispatchers.IO) { repo.labelsOf(paths) } }
        catch (e: Exception) { onError(e.message ?: e.toString()); onDismiss() }
    }
    val files = of?.values?.toList() ?: emptyList()
    val n = files.size
    fun has(id: String) = files.count { f -> f.cats.any { it.id == id } }
    fun autoOf(id: String) = files.count { f -> f.cats.any { it.id == id && it.auto } }
    fun hasTag(t: String) = files.count { f -> f.tags.any { it.equals(t, ignoreCase = true) } }
    fun state(now: Boolean?, count: Int) = when (now) {
        true -> ToggleableState.On; false -> ToggleableState.Off
        null -> if (count == 0) ToggleableState.Off else if (count == n) ToggleableState.On else ToggleableState.Indeterminate
    }
    val shownTags = (files.flatMap { it.tags } + tags.keys).distinctBy { it.lowercase() }.sortedBy { it.lowercase() }
    fun addTag() { val x = newTag.trim().removePrefix("#"); if (x.isNotEmpty()) tags = tags + (x to true); newTag = "" }
    AlertDialog(onDismissRequest = onDismiss,
        title = { Text(if (n > 1) stringResource(R.string.cats_labels_n, n) else stringResource(R.string.cats_labels)) },
        text = {
            val t = tree
            if (t == null || of == null) CircularProgressIndicator()
            else if (n == 0) Text(stringResource(R.string.cats_own_only))
            else Column(Modifier.heightIn(max = 480.dp).verticalScroll(rememberScrollState())) {
                if (files.any { f -> f.cats.any { it.auto } }) Text(stringResource(R.string.cats_auto_hint), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(bottom = 6.dp))
                for ((c, depth) in flat(t)) {
                    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(start = (depth * 16).dp)) {
                        TriStateCheckbox(state = state(cat[c.id], has(c.id)), onClick = {
                            val on = state(cat[c.id], has(c.id)) != ToggleableState.On
                            cat = cat + (c.id to on)
                        })
                        CatDot(c.color)
                        Spacer(Modifier.width(6.dp))
                        Text(c.name, Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                        if (autoOf(c.id) > 0 && cat[c.id] == null) Icon(Icons.Default.AutoAwesome, contentDescription = stringResource(R.string.cats_auto_mark),
                            tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(16.dp))
                        IconButton(onClick = { newCat = c.id }) { Icon(Icons.Default.Add, contentDescription = stringResource(R.string.cats_new_sub), modifier = Modifier.size(18.dp)) }
                    }
                }
                TextButton(onClick = { newCat = "" }) { Icon(Icons.Default.Add, contentDescription = null); Spacer(Modifier.width(6.dp)); Text(stringResource(R.string.cats_new)) }
                Text(stringResource(R.string.cats_tags), style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 8.dp, bottom = 4.dp))
                FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    for (x in shownTags) {
                        val st = state(tags[x], hasTag(x))
                        FilterChip(selected = st == ToggleableState.On, onClick = { tags = tags + (x to (st != ToggleableState.On)) },
                            label = { Text("#" + x + if (st == ToggleableState.Indeterminate) " (…)" else "") })
                    }
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(value = newTag, onValueChange = { newTag = it }, singleLine = true, label = { Text(stringResource(R.string.cats_add_tag)) }, modifier = Modifier.weight(1f))
                    IconButton(onClick = { addTag() }, enabled = newTag.isNotBlank()) { Icon(Icons.Default.Add, contentDescription = stringResource(R.string.add)) }
                }
                val suggestions = t.tags.map { it.tag }.filter { newTag.isNotBlank() && it.startsWith(newTag.trim(), ignoreCase = true) && shownTags.none { s -> s.equals(it, true) } }.take(6)
                if (suggestions.isNotEmpty()) FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    for (s in suggestions) TextButton(onClick = { tags = tags + (s to true); newTag = "" }) { Text("#$s") }
                }
            }
        },
        confirmButton = {
            TextButton(enabled = !busy && n > 0, onClick = {
                busy = true
                scope.launch {
                    try {
                        // Automatisch eingeordnete, die angehakt bleiben, gelten als bestätigt
                        val add = cat.filterValues { it }.keys.toMutableList()
                        tree?.cats?.forEach { c -> if (cat[c.id] == null && autoOf(c.id) > 0 && has(c.id) == n) add.add(c.id) }
                        withContext(Dispatchers.IO) {
                            repo.setLabels(LabelsSet(paths, add = add, remove = cat.filterValues { !it }.keys.toList(),
                                addTags = tags.filterValues { it }.keys.toList(), removeTags = tags.filterValues { !it }.keys.toList()))
                        }
                        onDone(); onDismiss()
                    } catch (e: Exception) { onError(e.message ?: e.toString()) } finally { busy = false }
                }
            }) { Text(stringResource(R.string.save)) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
    newCat?.let { parent ->
        NameDialog(initial = "", onDismiss = { newCat = null }) { name ->
            newCat = null
            scope.launch {
                try {
                    val t = tree
                    val color = parent.takeIf { it.isNotEmpty() }?.let { p -> t?.cats?.firstOrNull { it.id == p }?.color } ?: CAT_PALETTE[(t?.cats?.size ?: 0) % CAT_PALETTE.size]
                    val c = withContext(Dispatchers.IO) { repo.createCategory(name, parent.takeIf { it.isNotEmpty() }, color) }
                    loadTree()
                    cat = cat + (c.id to true)
                } catch (e: Exception) { onError(e.message ?: e.toString()) }
            }
        }
    }
}
