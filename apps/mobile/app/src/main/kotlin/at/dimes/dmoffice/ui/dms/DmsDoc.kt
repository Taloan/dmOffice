package at.dimes.dmoffice.ui.dms

import android.content.Intent
import android.graphics.Bitmap
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateCentroid
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.OpenInNew
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.LockOpen
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChanged
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Surface
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import at.dimes.dmoffice.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

/**
 * Ein Dokument: Angaben (Titel, Dokumentdatum, Korrespondent, Dokumenttyp, Tags) ändern - jede Änderung geht sofort an den
 * Server, der nach dem Ablageprofil neu ablegt -, bestätigen (aus dem Posteingang), ansehen, erkannter Text, in den Tresor
 * verschieben bzw. aus dem Tresor holen, löschen.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun DmsDocScreen(doc: DmsDoc, tresor: Boolean, tresorExists: Boolean, entriesIn: List<DmsEntry>, repo: DmsRepo, onClose: () -> Unit, onChanged: (DmsDoc) -> Unit,
    onConfirmed: () -> Unit = onClose) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    // Listen: neu geladen, wenn im Auswahldialog Einträge angelegt oder verschoben werden
    var entries by remember(entriesIn) { mutableStateOf(entriesIn) }
    var title by remember(doc.id) { mutableStateOf(doc.title) }
    var picking by remember { mutableStateOf<String?>(null) }
    var viewing by remember { mutableStateOf(false) }
    var menu by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf(false) }
    var confirmMove by remember { mutableStateOf(false) }
    var text by remember(doc.id) { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    var dates by remember(doc.id) { mutableStateOf<List<DmsDateCandidate>>(emptyList()) }
    var pickDate by remember { mutableStateOf(false) }
    BackHandler(enabled = !viewing, onBack = onClose)
    LaunchedEffect(doc.id, doc.state) { if (doc.state == "done") dates = try { repo.dates(doc.id, tresor) } catch (_: Exception) { emptyList() } }

    // Aktuellen Stand holen (die Liste kann veraltet sein, etwa nach der Texterkennung)
    LaunchedEffect(doc.id) { try { onChanged(repo.doc(doc.id, tresor)) } catch (_: Exception) { } }

    fun act(block: suspend () -> Unit) {
        busy = true
        scope.launch {
            try { block() } catch (e: Exception) { Toast.makeText(ctx, e.message ?: e.toString(), Toast.LENGTH_LONG).show() }
            finally { busy = false }
        }
    }
    fun change(c: DmsChange) = act { onChanged(repo.change(doc.id, tresor, c)) }

    if (viewing) { DmsViewer(doc, tresor, repo) { viewing = false }; return }

    Scaffold(contentWindowInsets = WindowInsets(0), topBar = {
        TopAppBar(windowInsets = WindowInsets(0), title = { Text(doc.title.ifBlank { doc.originalName }, maxLines = 1, overflow = TextOverflow.Ellipsis) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                IconButton(onClick = { viewing = true }, enabled = doc.state != "queued") { Icon(Icons.Default.Visibility, contentDescription = stringResource(R.string.dms_view)) }
                IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = null) }
                DropdownMenu(menu, { menu = false }) {
                    if (tresorExists) DropdownMenuItem(text = { Text(stringResource(if (tresor) R.string.dms_from_tresor else R.string.dms_to_tresor)) },
                        leadingIcon = { Icon(if (tresor) Icons.Default.LockOpen else Icons.Default.Lock, null) },
                        onClick = { menu = false; confirmMove = true })
                    DropdownMenuItem(text = { Text(stringResource(R.string.delete)) }, leadingIcon = { Icon(Icons.Default.Delete, null) },
                        onClick = { menu = false; confirmDelete = true })
                }
            })
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
            if (busy) androidx.compose.material3.LinearProgressIndicator(Modifier.fillMaxWidth())
            when (doc.state) {
                "queued" -> Text(stringResource(R.string.dms_queued), color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(16.dp))
                "error" -> Text(doc.error ?: stringResource(R.string.dms_error), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp))
            }
            if (doc.inbox) Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(if (doc.byAi) R.string.dms_inbox_ai else R.string.dms_inbox_hint), Modifier.weight(1f),
                    style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Button(onClick = { act { onChanged(repo.change(doc.id, tresor, DmsChange(confirm = true))); onConfirmed() } }, enabled = doc.state == "done") {
                    Icon(Icons.Default.Check, null); Text(stringResource(R.string.dms_confirm), Modifier.padding(start = 6.dp))
                }
            }
            OutlinedTextField(title, { title = it }, label = { Text(stringResource(R.string.dms_title)) }, singleLine = true,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
                trailingIcon = if (title.trim() != doc.title && title.isNotBlank()) { { IconButton(onClick = { change(DmsChange(title = title.trim())) }) {
                    Icon(Icons.Default.Check, contentDescription = stringResource(R.string.save)) } } } else null)
            // Betrag des Dokuments; mit Finanzen dazu die Buchung als Einnahme oder Ausgabe
            var amount by remember(doc.id, doc.amount) { mutableStateOf(doc.amount?.let { centsText(it) } ?: "") }
            OutlinedTextField(amount, { amount = it }, label = { Text(stringResource(R.string.dms_amount)) }, singleLine = true,
                suffix = { Text("€") }, keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(keyboardType = androidx.compose.ui.text.input.KeyboardType.Decimal),
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
                trailingIcon = if (amount != (doc.amount?.let { centsText(it) } ?: "")) { { IconButton(onClick = {
                    val c = parseCents(amount)
                    if (amount.isBlank()) change(DmsChange(amount = "")) else if (c != null) change(DmsChange(amount = c.toString()))
                    else Toast.makeText(ctx, ctx.getString(R.string.dms_amount_invalid), Toast.LENGTH_LONG).show()
                }) { Icon(Icons.Default.Check, contentDescription = stringResource(R.string.save)) } } } else null)
            if (at.dimes.dmoffice.data.Features.on("finance") && doc.state == "done") BookingField(doc, tresor, repo) { act { onChanged(repo.doc(doc.id, tresor)) } }
            val byId = remember(entries) { entries.associateBy { it.id } }
            // Mehrere Daten im Text: erst die Auswahl, sonst gleich der Kalender
            fun calendar() {
                val d = doc.docDate?.let { runCatching { java.time.LocalDate.parse(it) }.getOrNull() } ?: java.time.LocalDate.now()
                android.app.DatePickerDialog(ctx, { _, y, m, day -> change(DmsChange(docDate = java.time.LocalDate.of(y, m + 1, day).toString())) },
                    d.year, d.monthValue - 1, d.dayOfMonth).show()
            }
            Field(stringResource(R.string.dms_doc_date), doc.docDate?.let { dmsDate(it) },
                hint = if (dates.size > 1) ctx.resources.getQuantityString(R.plurals.dms_dates_found, dates.size, dates.size) else null) {
                if (dates.isNotEmpty() && dates.any { it.date != doc.docDate }) pickDate = true else calendar()
            }
            if (pickDate) AlertDialog(onDismissRequest = { pickDate = false }, title = { Text(stringResource(R.string.dms_doc_date)) },
                text = {
                    LazyColumn {
                        items(dates, key = { it.date }) { c ->
                            ListItem(modifier = Modifier.clickable { pickDate = false; if (c.date != doc.docDate) change(DmsChange(docDate = c.date)) },
                                leadingContent = if (c.date == doc.docDate) { { Icon(Icons.Default.Check, null) } } else null,
                                headlineContent = { Text(dmsDate(c.date) + if (c.count > 1) " · ${c.count}×" else "") },
                                supportingContent = { Text(c.context, maxLines = 2, overflow = TextOverflow.Ellipsis) })
                        }
                    }
                },
                confirmButton = { TextButton(onClick = { pickDate = false; calendar() }) { Text(stringResource(R.string.dms_other_date)) } },
                dismissButton = { TextButton(onClick = { pickDate = false }) { Text(stringResource(R.string.cancel)) } })
            Field(stringResource(R.string.dms_correspondent), fullName(entries, doc.correspondent).takeIf { it.isNotEmpty() }, auto = doc.autoCorrespondent && doc.correspondent != null) { picking = "correspondent" }
            Field(stringResource(R.string.dms_doctype), fullName(entries, doc.doctype).takeIf { it.isNotEmpty() }, auto = doc.autoDoctype && doc.doctype != null) { picking = "doctype" }
            Field(stringResource(R.string.dms_tags), doc.tags.mapNotNull { byId[it.id]?.name }.joinToString(", ").takeIf { it.isNotEmpty() }) { picking = "tag" }
            HorizontalDivider(Modifier.padding(vertical = 8.dp))
            Text(listOfNotNull(doc.path?.takeIf { !tresor }?.let { if (doc.mount != null) stringResource(R.string.dms_external) + " › " + it else it }, doc.originalName.takeIf { it != doc.path?.substringAfterLast('/') },
                android.text.format.Formatter.formatShortFileSize(ctx, doc.size)).joinToString("\n"),
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 16.dp))
            if (doc.sender != null) Text(stringResource(R.string.dms_sender, doc.sender), style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 16.dp, vertical = 2.dp))
            // Erkannter Text erst auf Tipp
            val t = text
            if (t == null) TextButton(onClick = { act { text = repo.text(doc.id, tresor) } }, modifier = Modifier.padding(horizontal = 8.dp)) {
                Text(stringResource(R.string.dms_show_text))
            } else Text(t.ifBlank { stringResource(R.string.dms_no_text) }, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(16.dp))
        }
    }

    picking?.let { kind ->
        val multi = kind == "tag"
        val current = when (kind) { "correspondent" -> listOfNotNull(doc.correspondent); "doctype" -> listOfNotNull(doc.doctype); else -> doc.tags.map { it.id } }
        EntryPicker(kind, entries, current.toSet(), multi, repo, onEntries = { entries = it }, onDismiss = { picking = null }) { chosen ->
            picking = null
            change(when (kind) {
                "correspondent" -> DmsChange(correspondent = chosen.firstOrNull() ?: "")
                "doctype" -> DmsChange(doctype = chosen.firstOrNull() ?: "")
                else -> DmsChange(tags = chosen.toList())
            })
        }
    }
    if (confirmDelete) AlertDialog(onDismissRequest = { confirmDelete = false },
        title = { Text(stringResource(R.string.dms_delete_title)) },
        text = { Text(stringResource(when { tresor -> R.string.dms_delete_tresor; doc.mount != null -> R.string.dms_delete_external; else -> R.string.dms_delete_text })) },
        confirmButton = { TextButton(onClick = { confirmDelete = false; act { repo.delete(listOf(doc.id), tresor); onClose() } }) { Text(stringResource(R.string.delete)) } },
        dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text(stringResource(R.string.cancel)) } })
    if (confirmMove) AlertDialog(onDismissRequest = { confirmMove = false },
        title = { Text(stringResource(if (tresor) R.string.dms_from_tresor else R.string.dms_to_tresor)) },
        text = { Text(stringResource(if (tresor) R.string.dms_from_tresor_text else R.string.dms_to_tresor_text)) },
        confirmButton = { TextButton(onClick = { confirmMove = false; act { repo.moveArea(listOf(doc.id), if (tresor) "files" else "tresor"); onClose() } }) {
            Text(stringResource(R.string.dms_move)) } },
        dismissButton = { TextButton(onClick = { confirmMove = false }) { Text(stringResource(R.string.cancel)) } })
}

/** Cent als Text („12,50“), wie ihn das Eingabefeld zeigt. */
private fun centsText(c: Long): String = String.format(java.util.Locale.GERMANY, "%.2f", c / 100.0)

/** „1.234,56“, „12.50“ oder „12“ in Cent; null, wenn es keine Zahl ist. */
internal fun parseCents(text: String): Long? {
    var v = text.replace(" ", "").replace("€", "")
    if (v.isEmpty()) return null
    val comma = v.lastIndexOf(','); val dot = v.lastIndexOf('.')
    v = when {
        comma >= 0 && dot >= 0 -> if (comma > dot) v.replace(".", "").replace(',', '.') else v.replace(",", "")
        comma >= 0 -> v.replace(',', '.')
        else -> v
    }
    if (!Regex("^-?\\d+(\\.\\d{1,2})?$").matches(v)) return null
    return java.math.BigDecimal(v).movePointRight(2).toLong()
}

/**
 * „In Finanzen“: als Einnahme oder Ausgabe buchen. Legt einen Eintrag mit der Kategorie „Noch nicht zugeordnet“ an und fixiert das
 * Dokument; Art und Betrag der Buchung lassen sich hier und in Finanzen ändern, der Betrag des Dokuments bleibt unberührt.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun BookingField(doc: DmsDoc, tresor: Boolean, repo: DmsRepo, onDocChanged: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var booking by remember(doc.id) { mutableStateOf<DmsBooking?>(null) }
    var loaded by remember(doc.id) { mutableStateOf(false) }
    var amount by remember(doc.id) { mutableStateOf("") }
    var confirmRemove by remember { mutableStateOf(false) }
    LaunchedEffect(doc.id) {
        booking = try { repo.booking(doc.id, tresor) } catch (_: Exception) { null }
        amount = booking?.let { centsText(it.amountCents) } ?: ""
        loaded = true
    }
    if (!loaded) return
    fun save(kind: String?, cents: Long? = null) = scope.launch {
        try {
            booking = repo.book(doc.id, tresor, kind, cents)
            amount = booking?.let { centsText(it.amountCents) } ?: ""
            onDocChanged()
        } catch (e: Exception) { Toast.makeText(ctx, e.message ?: e.toString(), Toast.LENGTH_LONG).show() }
    }
    val b = booking
    val locked = b != null && (b.closed || b.invoice)
    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp)) {
        Text(stringResource(R.string.dms_finance), style = MaterialTheme.typography.labelLarge)
        Row(verticalAlignment = Alignment.CenterVertically) {
            listOf(null to R.string.dms_not_booked, "income" to R.string.dms_income, "expense" to R.string.dms_expense).forEach { (k, label) ->
                androidx.compose.material3.FilterChip(selected = (b?.kind) == k, enabled = !locked, modifier = Modifier.padding(end = 6.dp),
                    onClick = { if ((b?.kind) != k) { if (k == null) confirmRemove = true else save(k) } }, label = { Text(stringResource(label)) })
            }
        }
        if (b != null) OutlinedTextField(amount, { amount = it }, label = { Text(stringResource(R.string.dms_finance_amount)) }, singleLine = true, enabled = !locked,
            suffix = { Text("€") }, keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(keyboardType = androidx.compose.ui.text.input.KeyboardType.Decimal),
            modifier = Modifier.fillMaxWidth(),
            trailingIcon = if (amount != centsText(b.amountCents)) { { IconButton(onClick = {
                val c = parseCents(amount)
                if (c == null || c == 0L) Toast.makeText(ctx, ctx.getString(R.string.dms_amount_invalid), Toast.LENGTH_LONG).show() else save(b.kind, kotlin.math.abs(c))
            }) { Icon(Icons.Default.Check, contentDescription = stringResource(R.string.save)) } } } else null)
        val hints = if (b == null) listOf(stringResource(R.string.dms_finance_hint)) else listOfNotNull(
            stringResource(R.string.dms_finance_invoice).takeIf { b.invoice },
            stringResource(R.string.dms_finance_closed).takeIf { b.closed },
            stringResource(R.string.dms_finance_unassigned).takeIf { b.unassigned },
            stringResource(R.string.dms_finance_differs).takeIf { doc.amount != null && doc.amount != b.amountCents })
        if (hints.isNotEmpty()) Text(hints.joinToString(" "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
    if (confirmRemove) AlertDialog(onDismissRequest = { confirmRemove = false },
        title = { Text(stringResource(R.string.dms_finance_remove)) }, text = { Text(stringResource(R.string.dms_finance_remove_text)) },
        confirmButton = { TextButton(onClick = { confirmRemove = false; save(null) }) { Text(stringResource(R.string.dms_finance_remove_ok)) } },
        dismissButton = { TextButton(onClick = { confirmRemove = false }) { Text(stringResource(R.string.cancel)) } })
}

@Composable
private fun Field(label: String, value: String?, auto: Boolean = false, hint: String? = null, onClick: () -> Unit) {
    ListItem(modifier = Modifier.clickable(onClick = onClick),
        overlineContent = { Text(label) },
        headlineContent = { Text(value ?: stringResource(R.string.dms_unset), maxLines = 2, overflow = TextOverflow.Ellipsis,
            color = if (value == null) MaterialTheme.colorScheme.onSurfaceVariant else Color.Unspecified) },
        supportingContent = if (auto || hint != null) { { Text(listOfNotNull(if (auto) stringResource(R.string.dms_auto) else null, hint).joinToString(" · ")) } } else null)
}

/** Name zum Vergleichen: ohne Leerzeichen, Groß/klein egal (wie auf dem Server). */
internal fun entryKey(name: String) = name.filterNot { it.isWhitespace() }.lowercase()

/**
 * Eintrag wählen (Korrespondent, Dokumenttyp) bzw. mehrere (Tags) - als Vollbild (Tablet, Querformat: breit in der Mitte).
 * Suchfeld oben filtert; gibt es den getippten Namen nicht, steht „„Name“ anlegen“ ganz oben. Unterbegriffe eingerückt.
 */
@Composable
@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class, ExperimentalMaterial3Api::class)
private fun EntryPicker(kind: String, entries: List<DmsEntry>, current: Set<String>, multi: Boolean, repo: DmsRepo, onEntries: (List<DmsEntry>) -> Unit,
                        onDismiss: () -> Unit, onDone: (Set<String>) -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var chosen by remember { mutableStateOf(current) }
    var acting by remember { mutableStateOf<DmsEntry?>(null) }
    acting?.let { e -> EntryActions(e, entries, repo, onDismiss = { acting = null }, onChanged = onEntries) }
    var filter by remember { mutableStateOf("") }
    var naming by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    val list = remember(entries, kind) { ordered(entries, kind) }
    val typed = filter.trim()
    val exists = typed.isNotEmpty() && list.any { entryKey(it.first.name) == entryKey(typed) }

    /** Anlegen und gleich wählen (Tags: dazunehmen). */
    fun create(name: String) {
        busy = true
        scope.launch {
            try {
                repo.createEntry(kind, name, null)
                val fresh = repo.lists()
                onEntries(fresh)
                val e = fresh.filter { it.kind == kind && entryKey(it.name) == entryKey(name) }.minByOrNull { if (it.parent == null) 0 else 1 }
                if (e != null) { if (multi) { chosen = chosen + e.id; filter = "" } else onDone(setOf(e.id)) }
            } catch (e: Exception) { Toast.makeText(ctx, e.message ?: e.toString(), Toast.LENGTH_LONG).show() }
            finally { busy = false }
        }
    }

    naming?.let { start ->
        var name by remember { mutableStateOf(start) }
        AlertDialog(onDismissRequest = { naming = null }, title = { Text(stringResource(R.string.dms_entry_new_title)) },
            text = { OutlinedTextField(name, { name = it }, singleLine = true, label = { Text(stringResource(R.string.dms_entry_name)) }, modifier = Modifier.fillMaxWidth()) },
            confirmButton = { TextButton(enabled = name.isNotBlank(), onClick = { naming = null; create(name.trim()) }) { Text(stringResource(R.string.save)) } },
            dismissButton = { TextButton(onClick = { naming = null }) { Text(stringResource(R.string.cancel)) } })
    }

    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        BoxWithConstraints(Modifier.fillMaxSize().systemBarsPadding(), contentAlignment = Alignment.Center) {
            val wide = maxWidth > 640.dp
            Surface(if (wide) Modifier.widthIn(max = 640.dp).fillMaxWidth().fillMaxHeight(0.85f) else Modifier.fillMaxSize(),
                shape = if (wide) MaterialTheme.shapes.extraLarge else androidx.compose.ui.graphics.RectangleShape) {
                Column(Modifier.fillMaxSize().imePadding()) {
                    TopAppBar(windowInsets = WindowInsets(0),
                        title = { Text(stringResource(when (kind) { "correspondent" -> R.string.dms_correspondent; "doctype" -> R.string.dms_doctype; else -> R.string.dms_tags })) },
                        navigationIcon = { IconButton(onClick = onDismiss) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } },
                        actions = { if (multi) TextButton(onClick = { onDone(chosen) }) { Text(stringResource(R.string.save)) } })
                    OutlinedTextField(filter, { filter = it }, singleLine = true, placeholder = { Text(stringResource(R.string.search)) },
                        leadingIcon = { Icon(Icons.Default.Search, null) },
                        trailingIcon = if (filter.isNotEmpty()) { { IconButton(onClick = { filter = "" }) { Icon(Icons.Default.Clear, contentDescription = null) } } } else null,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp))
                    if (busy) androidx.compose.material3.LinearProgressIndicator(Modifier.fillMaxWidth())
                    val shown = if (typed.isEmpty()) list else list.filter { it.first.name.contains(typed, ignoreCase = true) }
                    LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
                        if (typed.isNotEmpty() && !exists) item {
                            ListItem(modifier = Modifier.clickable(enabled = !busy) { create(typed) },
                                leadingContent = { Icon(Icons.Default.Add, null, tint = MaterialTheme.colorScheme.primary) },
                                headlineContent = { Text(stringResource(R.string.dms_entry_create, typed), color = MaterialTheme.colorScheme.primary) })
                        }
                        if (!multi && typed.isEmpty()) item {
                            ListItem(modifier = Modifier.clickable { onDone(emptySet()) }, headlineContent = { Text(stringResource(R.string.dms_unset)) },
                                leadingContent = if (current.isEmpty()) { { Icon(Icons.Default.Check, null) } } else null)
                        }
                        items(shown, key = { it.first.id }) { (e, depth) ->
                            val on = e.id in chosen
                            ListItem(modifier = Modifier.combinedClickable(onLongClick = { acting = e }) {
                                    if (multi) chosen = if (on) chosen - e.id else chosen + e.id else onDone(setOf(e.id))
                                },
                                headlineContent = { Text(e.name, Modifier.padding(start = (if (typed.isEmpty()) depth * 24 else 0).dp)) },
                                supportingContent = if (typed.isNotEmpty() && e.parent != null) { { Text(parentPath(entries, e)) } } else null,
                                leadingContent = if (multi) { { Checkbox(on, null) } } else if (on) { { Icon(Icons.Default.Check, null) } } else null)
                        }
                        if (list.isEmpty() && typed.isEmpty()) item {
                            Text(stringResource(R.string.dms_list_empty), Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        item {
                            ListItem(modifier = Modifier.clickable(enabled = !busy) { naming = typed },
                                leadingContent = { Icon(Icons.Default.Add, null) },
                                headlineContent = { Text(stringResource(R.string.dms_entry_new)) })
                        }
                        if (list.isNotEmpty()) item {
                            Text(stringResource(R.string.dms_entry_hint), Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        }
    }
}

/** Übergeordnete Einträge, etwa „Versicherung“ für „Kfz“ (beim Suchen unter dem Namen). */
private fun parentPath(all: List<DmsEntry>, e: DmsEntry): String {
    val byId = all.associateBy { it.id }
    val names = mutableListOf<String>()
    var p = e.parent?.let { byId[it] }
    while (p != null && names.size < 20) { names.add(0, p.name); p = p.parent?.let { byId[it] } }
    return names.joinToString(" › ")
}

/**
 * Ansehen: PDF Seite für Seite (PdfRenderer), Bilder; sonst „Öffnen mit“. Die Datei liegt nur für die Ansicht im
 * Zwischenspeicher und wird beim Schließen gelöscht. Aus dem Tresor geht nichts an andere Apps.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun DmsViewer(doc: DmsDoc, tresor: Boolean, repo: DmsRepo, onClose: () -> Unit) {
    val ctx = LocalContext.current
    BackHandler(onBack = onClose)
    val name = doc.path?.substringAfterLast('/') ?: doc.originalName
    val file = remember { File(File(ctx.cacheDir, "files/dms").apply { mkdirs() }, "view-${doc.id}-" + name.replace('/', '_')) }
    var ready by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) { try { repo.download(doc, tresor, file); ready = true } catch (e: Exception) { error = e.message } }
    DisposableEffect(Unit) { onDispose { file.delete() } }
    val mime = if (name.endsWith(".pdf", true)) "application/pdf" else doc.mime ?: "application/octet-stream"

    Scaffold(contentWindowInsets = WindowInsets(0), topBar = {
        TopAppBar(windowInsets = WindowInsets(0), title = { Text(doc.title.ifBlank { name }, maxLines = 1, overflow = TextOverflow.Ellipsis) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                if (!tresor && ready) IconButton(onClick = {
                    val uri = androidx.core.content.FileProvider.getUriForFile(ctx, ctx.packageName + ".files", file)
                    try { ctx.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)) }
                    catch (_: Exception) { Toast.makeText(ctx, R.string.dms_no_app, Toast.LENGTH_SHORT).show() }
                }) { Icon(Icons.AutoMirrored.Filled.OpenInNew, contentDescription = stringResource(R.string.dms_open_with)) }
            })
    }) { pad ->
        Box(Modifier.padding(pad).fillMaxSize().background(MaterialTheme.colorScheme.surfaceVariant), contentAlignment = Alignment.Center) {
            when {
                error != null -> Text(stringResource(R.string.error_generic, error!!), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp))
                !ready -> CircularProgressIndicator()
                mime == "application/pdf" -> PdfPages(file)
                mime.startsWith("text/") || name.endsWith(".txt", true) || name.endsWith(".md", true) -> {
                    val content = remember { file.bufferedReader().use { rd -> CharArray(200_000).let { b -> String(b, 0, rd.read(b).coerceAtLeast(0)) } } }
                    Text(content, Modifier.fillMaxSize().background(MaterialTheme.colorScheme.surface).verticalScroll(rememberScrollState()).padding(16.dp),
                        style = MaterialTheme.typography.bodyMedium)
                }
                mime.startsWith("image/") -> {
                    val bmp = remember { android.graphics.BitmapFactory.decodeFile(file.path, android.graphics.BitmapFactory.Options().apply { inSampleSize = sampleFor(file) }) }
                    if (bmp != null) Image(bmp.asImageBitmap(), contentDescription = null, modifier = Modifier.fillMaxSize().previewZoom(), filterQuality = FilterQuality.High)
                }
                else -> Text(stringResource(if (tresor) R.string.dms_no_preview_tresor else R.string.dms_no_preview), Modifier.padding(24.dp))
            }
        }
    }
}

/** Großes Bild verkleinert laden (höchstens etwa 2000 px). */
private fun sampleFor(f: File): Int {
    val o = android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds = true }
    android.graphics.BitmapFactory.decodeFile(f.path, o)
    var s = 1
    while (maxOf(o.outWidth, o.outHeight) / s > 2000) s *= 2
    return s
}

/**
 * Zoom der Vorschau: Zwei Finger zoomen, Doppeltipp zoomt hinein oder zurück. Gezoomt verschiebt ein Finger den Ausschnitt;
 * am oberen und unteren Rand geht die Bewegung an [onScrollPast] (PDF: zur nächsten Seite). Ungezoomt bleibt das Rollen unberührt.
 */
@Composable
private fun Modifier.previewZoom(onScrollPast: ((Float) -> Unit)? = null): Modifier {
    var scale by remember { mutableFloatStateOf(1f) }
    var offset by remember { mutableStateOf(Offset.Zero) }
    val scrollPast by rememberUpdatedState(onScrollPast)
    return this.clipToBounds()
        .pointerInput(Unit) {
            detectTapGestures(onDoubleTap = { p ->
                if (scale > 1.01f) { scale = 1f; offset = Offset.Zero }
                else {
                    scale = 2.5f
                    val o = (Offset(size.width / 2f, size.height / 2f) - p) * 1.5f
                    val maxX = size.width * 0.75f; val maxY = size.height * 0.75f
                    offset = Offset(o.x.coerceIn(-maxX, maxX), o.y.coerceIn(-maxY, maxY))
                }
            })
        }
        .pointerInput(Unit) {
            awaitEachGesture {
                awaitFirstDown(requireUnconsumed = false, pass = PointerEventPass.Initial)
                do {
                    // Vor der Liste auswerten: Zwei Finger und gezoomtes Verschieben dürfen die Liste nicht rollen
                    val event = awaitPointerEvent(PointerEventPass.Initial)
                    val fingers = event.changes.count { it.pressed }
                    if (fingers > 1 || scale > 1.01f) {
                        val s = (scale * event.calculateZoom()).coerceIn(1f, 6f)
                        val pan = event.calculatePan()
                        val c = event.calculateCentroid()
                        val mid = Offset(size.width / 2f, size.height / 2f)
                        val o = if (c == Offset.Unspecified) offset else (offset - (c - mid)) * (s / scale) + (c - mid)
                        val wantY = o.y + pan.y
                        val maxX = size.width * (s - 1) / 2; val maxY = size.height * (s - 1) / 2
                        val y = wantY.coerceIn(-maxY, maxY)
                        scale = s
                        offset = if (s <= 1.01f) Offset.Zero else Offset((o.x + pan.x).coerceIn(-maxX, maxX), y)
                        if (fingers == 1 && wantY != y) scrollPast?.invoke((wantY - y) / s)
                        event.changes.forEach { if (it.positionChanged()) it.consume() }
                    }
                } while (event.changes.any { it.pressed })
            }
        }
        .graphicsLayer { scaleX = scale; scaleY = scale; translationX = offset.x; translationY = offset.y }
}

/** PDF-Seiten untereinander, erst gerendert, wenn sie sichtbar werden: in doppelter Bildschirmbreite, damit Text beim Zoomen lesbar bleibt. */
@Composable
private fun PdfPages(file: File) {
    val renderer = remember { try { PdfRenderer(ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY)) } catch (_: Exception) { null } }
    DisposableEffect(renderer) { onDispose { synchronized(file) { renderer?.close() } } }
    if (renderer == null) { Text(stringResource(R.string.dms_no_preview)); return }
    val width = with(LocalDensity.current) { LocalConfiguration.current.screenWidthDp.dp.roundToPx() }
    val list = rememberLazyListState()
    LazyColumn(Modifier.fillMaxSize().previewZoom { list.dispatchRawDelta(-it) }, state = list, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        items((0 until renderer.pageCount).toList()) { i ->
            var bmp by remember { mutableStateOf<Bitmap?>(null) }
            var ratio by remember { mutableStateOf(0.707f) }
            LaunchedEffect(i) {
                bmp = withContext(Dispatchers.IO) {
                    synchronized(file) {
                        renderer.openPage(i).use { p ->
                            ratio = p.width.toFloat() / p.height
                            fun blank(w: Int) = Bitmap.createBitmap(w, (w / ratio).toInt().coerceAtLeast(1), Bitmap.Config.ARGB_8888)
                            val b = try { blank(width * 2) } catch (_: OutOfMemoryError) { blank(width) }
                            b.eraseColor(android.graphics.Color.WHITE)
                            p.render(b, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY)
                            b
                        }
                    }
                }
            }
            Box(Modifier.fillMaxWidth().aspectRatio(ratio).background(Color.White)) {
                bmp?.let { Image(it.asImageBitmap(), contentDescription = null, modifier = Modifier.fillMaxSize(), filterQuality = FilterQuality.High) }
            }
        }
    }
}
