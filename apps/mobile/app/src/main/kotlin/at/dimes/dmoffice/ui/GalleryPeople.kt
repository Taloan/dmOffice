package at.dimes.dmoffice.ui

import android.graphics.Bitmap
import android.util.LruCache
import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SuggestionChip
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
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
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.files.FaceGroup
import at.dimes.dmoffice.files.FacePhotos
import at.dimes.dmoffice.files.FacesStatus
import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.Thumbs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Ausschnitte der Gesichter im Speicher - die Übersicht zeigt viele davon. */
private object FaceCrops {
    private val cache = LruCache<String, Bitmap>(200)
    fun cached(id: Long, size: Int): Bitmap? = cache.get("$id/$size")
    fun put(id: Long, size: Int, b: Bitmap) { cache.put("$id/$size", b) }
}

/** Gewählt: eine benannte Person oder eine Gruppe ohne Namen */
private sealed class PeopleSel {
    data class Person(val id: Long, val name: String) : PeopleSel()
    data class Group(val id: String) : PeopleSel()
}

/**
 * Personen in der Galerie (wie im Web): erkannte Gesichter nach Personen und Gruppen ohne Namen. Gruppen schnell zuordnen -
 * Gesicht antippen und Namen geben (danach gleich die nächste), Vorschlag aus bisherigen Zuordnungen mit ✓/✕ übernehmen oder ablehnen,
 * Leute, die keine Rolle spielen, ausblenden (auch auf späteren Fotos, mit Rückgängig). Person öffnen zeigt ihre Fotos, falsch
 * Zugeordnetes lässt sich lösen, Vorschläge bestätigen.
 */
@Composable
fun GalleryPeople(onOpen: (FileItem, List<FileItem>) -> Unit) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).files
    val scope = rememberCoroutineScope()
    var status by remember { mutableStateOf<FacesStatus?>(null) }
    var tick by remember { mutableIntStateOf(0) }
    var sel by remember { mutableStateOf<PeopleSel?>(null) }
    var busy by remember { mutableStateOf(false) }
    var lastHidden by remember { mutableStateOf<String?>(null) }
    var naming by remember { mutableStateOf<FaceGroup?>(null) }
    // Nach dem Benennen über den Dialog gleich die Gruppe an derselben Stelle anbieten (schnelles Durchgehen)
    var nextAt by remember { mutableStateOf<Int?>(null) }
    var askOff by remember { mutableStateOf(false) }
    val toast = { m: String -> Toast.makeText(ctx, m, Toast.LENGTH_LONG).show() }
    fun act(block: suspend () -> Unit, after: () -> Unit = {}) = scope.launch {
        busy = true
        try { withContext(Dispatchers.IO) { block() }; after(); tick++ } catch (e: Exception) { toast(e.message ?: e.toString()) } finally { busy = false }
    }

    LaunchedEffect(tick) {
        try {
            status = withContext(Dispatchers.IO) { repo.facesStatus() }
            nextAt?.let { i -> naming = status?.overview?.groups?.getOrNull(i); nextAt = null }
            // Liest der Server noch ein: ab und zu neu holen
            while (status?.running == true) { delay(4000); status = withContext(Dispatchers.IO) { repo.facesStatus() } }
        } catch (e: Exception) { toast(e.message ?: e.toString()) }
    }

    val st = status
    if (st == null) { Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }; return }
    val names = st.overview?.persons?.map { it.name } ?: emptyList()

    sel?.let { s ->
        BackHandler { sel = null; tick++ }
        PeopleDetail(s, names, onBack = { sel = null; tick++ }, onSel = { sel = it }, onOpen = onOpen)
        return
    }

    if (!st.enabled) {
        Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(stringResource(R.string.people_title), style = MaterialTheme.typography.titleLarge)
            Text(stringResource(R.string.people_intro))
            if (st.tresor.exists) Button(onClick = { act({ repo.facesEnable(true) }) }, enabled = !busy) { Text(stringResource(R.string.people_enable)) }
            else Text(stringResource(R.string.people_need_tresor), color = MaterialTheme.colorScheme.primary)
        }
        return
    }
    // Eingeschaltet, aber der Tresor ist auf diesem Handy zu: Personen erst nach dem Öffnen
    if (!st.tresor.open) {
        Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(stringResource(R.string.people_title), style = MaterialTheme.typography.titleLarge)
            Text(stringResource(R.string.people_tresor_closed))
            Button(onClick = { scope.launch { if (requestTresorOpen()) tick++ } }) { Text(stringResource(R.string.tresor_open)) }
        }
        return
    }

    val ov = st.overview
    val persons = ov?.persons?.filter { !it.hidden } ?: emptyList()
    val groups = ov?.groups ?: emptyList()
    val full: (androidx.compose.foundation.lazy.grid.LazyGridItemSpanScope.() -> GridItemSpan) = { GridItemSpan(maxLineSpan) }
    LazyVerticalGrid(GridCells.Adaptive(100.dp), Modifier.fillMaxSize(), contentPadding = androidx.compose.foundation.layout.PaddingValues(12.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        item(span = full) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.people_scanned, (ov?.scanned ?: 0).toInt()), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f))
                if (st.running) { CircularProgressIndicator(Modifier.size(14.dp), strokeWidth = 2.dp); Spacer(Modifier.width(6.dp))
                    Text(stringResource(R.string.people_running), style = MaterialTheme.typography.bodySmall) }
                TextButton(onClick = { askOff = true }) { Text(stringResource(R.string.people_off)) }
            }
        }
        items(persons, key = { "p" + it.id }) { p ->
            Column(Modifier.clickable { sel = PeopleSel.Person(p.id, p.name) }, horizontalAlignment = Alignment.CenterHorizontally) {
                FaceCircle(p.face, 84)
                Text(p.name, fontWeight = FontWeight.SemiBold, style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(stringResource(R.string.people_photos, p.count), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        if (groups.isNotEmpty() || lastHidden != null || (ov?.ignored ?: 0) > 0) {
            item(span = full) {
                Column {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(stringResource(R.string.people_unnamed), style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
                        lastHidden?.let { g ->
                            Text(stringResource(R.string.people_hidden), style = MaterialTheme.typography.bodySmall)
                            TextButton(onClick = { act({ repo.unignore(g) }) { lastHidden = null } }, enabled = !busy) { Text(stringResource(R.string.people_undo)) }
                        }
                    }
                    Text(stringResource(R.string.people_quick_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    if ((ov?.ignored ?: 0) > 0) TextButton(onClick = { act({ repo.unignore(null) }) { lastHidden = null } }, enabled = !busy) {
                        Text(stringResource(R.string.people_show_hidden, ov!!.ignored))
                    }
                }
            }
            items(groups, key = { it.id }) { g ->
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Box {
                        Box(Modifier.clickable { naming = g }) { FaceCircle(g.face, 84) }
                        IconButton(onClick = { act({ repo.ignoreGroup(g.id) }) { lastHidden = g.id } }, enabled = !busy,
                            modifier = Modifier.align(Alignment.TopEnd).size(28.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.55f))) {
                            Icon(Icons.Default.VisibilityOff, contentDescription = stringResource(R.string.people_hide), tint = Color.White, modifier = Modifier.size(16.dp))
                        }
                    }
                    Text(stringResource(R.string.people_photos, g.count), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    g.suggest?.let { sug ->
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text("${sug.name}?", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.SemiBold, style = MaterialTheme.typography.bodySmall,
                                maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                            IconButton(onClick = { act({ repo.nameGroup(g.id, sug.name) }) { toast(ctx.getString(R.string.people_named, sug.name)) } },
                                enabled = !busy, modifier = Modifier.size(28.dp)) {
                                Icon(Icons.Default.Check, contentDescription = stringResource(R.string.people_yes, sug.name), modifier = Modifier.size(18.dp))
                            }
                            IconButton(onClick = { act({ repo.rejectGroup(g.id, sug.id) }) }, enabled = !busy, modifier = Modifier.size(28.dp)) {
                                Icon(Icons.Default.Close, contentDescription = stringResource(R.string.people_no, sug.name), modifier = Modifier.size(18.dp))
                            }
                        }
                    }
                }
            }
        }
        if (persons.isEmpty() && groups.isEmpty() && !st.running) item(span = full) {
            Text(stringResource(R.string.people_none), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }

    naming?.let { g ->
        val index = groups.indexOfFirst { it.id == g.id }
        NameDialog(g, names, busy,
            onName = { n -> naming = null; act({ repo.nameGroup(g.id, n) }) { toast(ctx.getString(R.string.people_named, n)); nextAt = index } },
            onShow = { naming = null; sel = PeopleSel.Group(g.id) },
            onHide = { naming = null; act({ repo.ignoreGroup(g.id) }) { lastHidden = g.id } },
            onDismiss = { naming = null })
    }
    if (askOff) ConfirmDialog(stringResource(R.string.people_off), stringResource(R.string.people_off_ask), stringResource(R.string.people_off),
        onDismiss = { askOff = false }, onConfirm = { askOff = false; act({ repo.facesEnable(false) }) })
}

/** Namen geben: Feld mit passenden vorhandenen Namen zum Antippen, dazu Fotos zeigen und Ausblenden. */
@Composable
private fun NameDialog(g: FaceGroup, names: List<String>, busy: Boolean, onName: (String) -> Unit, onShow: () -> Unit, onHide: () -> Unit, onDismiss: () -> Unit) {
    var text by remember(g.id) { mutableStateOf("") }
    val focus = remember { FocusRequester() }
    LaunchedEffect(g.id) { try { focus.requestFocus() } catch (_: Exception) { } }
    val matches = remember(text, names) {
        val q = text.trim()
        (if (q.isEmpty()) names else names.filter { it.contains(q, ignoreCase = true) && !it.equals(q, ignoreCase = true) }).take(12)
    }
    AlertDialog(onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.people_who)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) { FaceCircle(g.face, 120) }
                OutlinedTextField(text, { text = it }, singleLine = true, modifier = Modifier.fillMaxWidth().focusRequester(focus),
                    placeholder = { Text(stringResource(R.string.people_who)) },
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Words, imeAction = ImeAction.Done),
                    keyboardActions = KeyboardActions(onDone = { if (text.isNotBlank()) onName(text.trim()) }))
                if (matches.isNotEmpty()) LazyRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    items(matches) { n -> SuggestionChip(onClick = { onName(n) }, label = { Text(n) }) }
                }
                g.suggest?.let { s -> TextButton(onClick = { onName(s.name) }) { Text(stringResource(R.string.people_yes, s.name)) } }
                Row {
                    TextButton(onClick = onShow) { Text(stringResource(R.string.people_show_photos)) }
                    TextButton(onClick = onHide) { Text(stringResource(R.string.people_hide), maxLines = 1, overflow = TextOverflow.Ellipsis) }
                }
            }
        },
        confirmButton = { Button(onClick = { onName(text.trim()) }, enabled = !busy && text.isNotBlank()) { Text(stringResource(R.string.people_name)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

/** Fotos einer Person oder Gruppe; bei Personen „Ist nicht …“ je Foto und Vorschläge zum Bestätigen. */
@Composable
private fun PeopleDetail(sel: PeopleSel, names: List<String>, onBack: () -> Unit, onSel: (PeopleSel) -> Unit, onOpen: (FileItem, List<FileItem>) -> Unit) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).files
    val scope = rememberCoroutineScope()
    var data by remember(sel) { mutableStateOf<FacePhotos?>(null) }
    var tick by remember { mutableIntStateOf(0) }
    var busy by remember { mutableStateOf(false) }
    var rename by remember { mutableStateOf<String?>(null) }
    var askRemove by remember { mutableStateOf(false) }
    var naming by remember { mutableStateOf(false) }
    val toast = { m: String -> Toast.makeText(ctx, m, Toast.LENGTH_LONG).show() }
    fun act(block: suspend () -> Unit, after: () -> Unit = {}) = scope.launch {
        busy = true
        try { withContext(Dispatchers.IO) { block() }; after(); tick++ } catch (e: Exception) { toast(e.message ?: e.toString()) } finally { busy = false }
    }
    LaunchedEffect(sel, tick) {
        data = try {
            withContext(Dispatchers.IO) { when (sel) { is PeopleSel.Person -> repo.personPhotos(sel.id); is PeopleSel.Group -> repo.groupPhotos(sel.id) } }
        } catch (e: Exception) { toast(e.message ?: e.toString()); onBack(); null }
    }
    val person = sel as? PeopleSel.Person
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.back)) }
            Text(person?.name ?: stringResource(R.string.people_unnamed), style = MaterialTheme.typography.titleMedium, maxLines = 1,
                overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            data?.let { Text(stringResource(R.string.people_photos, it.items.size), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            if (person != null) {
                IconButton(onClick = { rename = person.name }) { Icon(Icons.Default.Edit, contentDescription = stringResource(R.string.people_rename)) }
                IconButton(onClick = { askRemove = true }, enabled = !busy) { Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.people_remove)) }
            } else {
                TextButton(onClick = { naming = true }, enabled = !busy) { Text(stringResource(R.string.people_name)) }
                IconButton(onClick = { act({ repo.ignoreGroup((sel as PeopleSel.Group).id) }) { onBack() } }, enabled = !busy) {
                    Icon(Icons.Default.VisibilityOff, contentDescription = stringResource(R.string.people_hide))
                }
            }
        }
        val d = data
        if (d == null) { Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }; return@Column }
        val full: (androidx.compose.foundation.lazy.grid.LazyGridItemSpanScope.() -> GridItemSpan) = { GridItemSpan(maxLineSpan) }
        LazyVerticalGrid(GridCells.Adaptive(110.dp), Modifier.fillMaxSize(), contentPadding = androidx.compose.foundation.layout.PaddingValues(4.dp),
            horizontalArrangement = Arrangement.spacedBy(4.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            items(d.items, key = { it.path }) { i ->
                Box(Modifier.aspectRatio(1f).clip(RoundedCornerShape(4.dp)).background(MaterialTheme.colorScheme.surfaceVariant).clickable { onOpen(i, d.items) }) {
                    PhotoThumb(i)
                    if (person != null) IconButton(onClick = { act({ repo.rejectFaces(person.id, d.faces[i.path] ?: emptyList()) }) }, enabled = !busy,
                        modifier = Modifier.align(Alignment.TopEnd).padding(4.dp).size(28.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.55f))) {
                        Icon(Icons.Default.Close, contentDescription = stringResource(R.string.people_not, person.name), tint = Color.White, modifier = Modifier.size(16.dp))
                    }
                }
            }
            if (person != null && d.suggestions.isNotEmpty()) {
                item(span = full) { Text(stringResource(R.string.people_is_also, person.name), style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 12.dp, start = 4.dp)) }
                items(d.suggestions, key = { "s" + it.face }) { s ->
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        FaceCircle(s.face, 72)
                        Row {
                            IconButton(onClick = { act({ repo.confirmFaces(person.id, listOf(s.face)) }) }, enabled = !busy) {
                                Icon(Icons.Default.Check, contentDescription = stringResource(R.string.people_yes, person.name))
                            }
                            IconButton(onClick = { act({ repo.rejectFaces(person.id, listOf(s.face)) }) }, enabled = !busy) {
                                Icon(Icons.Default.Close, contentDescription = stringResource(R.string.people_no, person.name))
                            }
                        }
                    }
                }
            }
        }
    }

    rename?.let { cur ->
        var text by remember { mutableStateOf(cur) }
        AlertDialog(onDismissRequest = { rename = null }, title = { Text(stringResource(R.string.people_rename)) },
            text = { OutlinedTextField(text, { text = it }, singleLine = true, keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Words)) },
            confirmButton = {
                Button(onClick = {
                    val n = text.trim(); rename = null
                    act({ val id = repo.renamePerson(person!!.id, n); withContext(Dispatchers.Main) { onSel(PeopleSel.Person(id, n)) } })
                }, enabled = text.isNotBlank() && !busy) { Text(stringResource(R.string.save)) }
            },
            dismissButton = { TextButton(onClick = { rename = null }) { Text(stringResource(R.string.cancel)) } })
    }
    if (askRemove && person != null) ConfirmDialog(stringResource(R.string.people_remove), stringResource(R.string.people_remove_ask, person.name), stringResource(R.string.people_remove),
        onDismiss = { askRemove = false }, onConfirm = { askRemove = false; act({ repo.removePerson(person.id) }) { onBack() } })
    if (naming && sel is PeopleSel.Group) {
        NameDialog(FaceGroup(sel.id, data?.items?.size ?: 0, data?.faces?.values?.firstOrNull()?.firstOrNull()), names, busy,
            onName = { n -> naming = false; act({ val id = repo.nameGroup(sel.id, n); withContext(Dispatchers.Main) { toast(ctx.getString(R.string.people_named, n)); onSel(PeopleSel.Person(id, n)) } }) },
            onShow = { naming = false }, onHide = { naming = false; act({ repo.ignoreGroup(sel.id) }) { onBack() } }, onDismiss = { naming = false })
    }
}

/** Rundes Gesicht vom Server (Ausschnitt), solange es lädt eine leere Fläche. */
@Composable
private fun FaceCircle(id: Long?, size: Int) {
    val ctx = LocalContext.current
    val px = if (size > 96) 256 else 192
    val bmp by produceState(id?.let { FaceCrops.cached(it, px) }, id) {
        if (id != null && value == null) value = try {
            withContext(Dispatchers.IO) { App.get(ctx).files.faceCrop(id, px) }?.also { FaceCrops.put(id, px, it) }
        } catch (_: Exception) { null }
    }
    Box(Modifier.size(size.dp).clip(CircleShape).background(MaterialTheme.colorScheme.surfaceVariant)) {
        bmp?.let { Image(it.asImageBitmap(), contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize()) }
    }
}

/** Vorschaubild eines Fotos (Zwischenspeicher der Galerie). */
@Composable
private fun PhotoThumb(item: FileItem) {
    val ctx = LocalContext.current
    val bmp by produceState(Thumbs.cached(item, Thumbs.SMALL), item.path) {
        if (value == null) value = try { Thumbs.load(ctx, item, Thumbs.SMALL) } catch (_: Exception) { null }
    }
    bmp?.let { Image(it.asImageBitmap(), contentDescription = item.name, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize()) }
}
