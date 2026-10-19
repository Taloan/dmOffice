package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
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
import at.dimes.dmoffice.data.CalendarEntity
import at.dimes.dmoffice.data.ItemEntity
import at.dimes.dmoffice.data.Items
import at.dimes.dmoffice.data.ListTypes
import androidx.compose.runtime.collectAsState
import at.dimes.dmoffice.ical.IcalTime
import kotlinx.coroutines.launch
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId

/** Aufgabe anlegen oder bearbeiten: Titel, Kalender, Faelligkeit (Datum, optional Uhrzeit), Prioritaet, Erinnerung, Notizen.
 *  [list]: Vorgabe-Liste fuer eine neue Aufgabe (z. B. die Einkaufsliste aus dem Widget). */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TaskEditor(href: String?, onClose: () -> Unit, list: String? = null) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val zone = ZoneId.systemDefault()

    var loaded by remember { mutableStateOf(false) }
    var existing by remember { mutableStateOf<ItemEntity?>(null) }
    var calendars by remember { mutableStateOf<List<CalendarEntity>>(emptyList()) }
    var calendar by remember { mutableStateOf<CalendarEntity?>(null) }
    var title by remember { mutableStateOf("") }
    var notes by remember { mutableStateOf("") }
    var hasDue by remember { mutableStateOf(false) }
    var withTime by remember { mutableStateOf(false) }
    var dueDate by remember { mutableStateOf(LocalDate.now()) }
    var dueTime by remember { mutableStateOf(LocalTime.of(9, 0)) }
    var priority by remember { mutableStateOf(0) }
    var completed by remember { mutableStateOf(false) }
    var reminder by remember { mutableStateOf<Long?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    // Einkaufs-/Checkliste: Artikel, Menge, Einheit, Abteilung bzw. Kategorie statt Titel, Faelligkeit und Prioritaet (wie im Web)
    var qty by remember { mutableStateOf("") }
    var unit by remember { mutableStateOf("") }
    var article by remember { mutableStateOf("") }
    var section by remember { mutableStateOf("") }
    val types by ListTypes.byUri.collectAsState()
    LaunchedEffect(Unit) { ListTypes.refresh(ctx) }
    val info = calendar?.let { types[ListTypes.uriOf(it.href)] }?.takeIf { it.type != ListTypes.TASKS }
    val isCheck = info?.type == ListTypes.CHECK
    var confirmDelete by remember { mutableStateOf(false) }

    LaunchedEffect(href) {
        val cals = app.repo.calendarList()
        calendars = cals.filter { it.supportsTasks }.ifEmpty { cals }
        if (href != null) {
            val it = app.repo.item(href)
            if (it == null) { onClose(); return@LaunchedEffect }
            existing = it
            calendar = cals.firstOrNull { c -> c.href == it.calendarHref }
            title = it.summary; notes = it.description ?: ""
            ListTypes.itemOf(it.ics, it.summary).let { x -> qty = x.qty; unit = x.unit; article = x.item }
            section = ListTypes.sectionOf(it.ics) ?: ""
            priority = it.priority; completed = it.completed; reminder = it.reminderSeconds
            if (it.due != null) {
                hasDue = true; withTime = !it.allDay
                val d = LocalDateTime.ofInstant(java.time.Instant.ofEpochMilli(it.due), zone)
                dueDate = d.toLocalDate(); dueTime = d.toLocalTime()
            }
        } else calendar = calendars.firstOrNull { it.href == list } ?: calendars.firstOrNull { it.visible } ?: calendars.firstOrNull()
        loaded = true
    }

    fun save() {
        val cal = calendar ?: return
        if (info != null) {
            val x = ListTypes.ShopItem(qty.trim(), unit.trim(), article.trim())
            if (x.item.isEmpty()) { error = ctx.getString(R.string.title_required); return }
            scope.launch {
                app.repo.saveTask(existing?.href, cal.href, Items.TaskInput(x.title(), notes.trim().ifEmpty { null }, null, 0, completed, null,
                    item = x.item, unit = x.unit, qty = x.qty, section = section))
                onClose()
            }
            return
        }
        if (title.isBlank()) { error = ctx.getString(R.string.title_required); return }
        val due = when {
            !hasDue -> null
            withTime -> IcalTime(null, dueDate.atTime(dueTime).atZone(zone).toInstant(), zone)
            else -> IcalTime(dueDate, null, null)
        }
        scope.launch {
            app.repo.saveTask(existing?.href, cal.href, Items.TaskInput(title.trim(), notes.trim().ifEmpty { null }, due, priority, completed, if (hasDue) reminder else null))
            onClose()
        }
    }

    val priorities = listOf(0, 1, 5, 9)

    Scaffold(topBar = {
        TopAppBar(
            title = { Text(stringResource(if (existing == null) R.string.new_task else R.string.edit_task)) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                if (existing != null) IconButton(onClick = { confirmDelete = true }) { Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.delete)) }
                TextButton(onClick = { save() }, enabled = loaded) { Text(stringResource(R.string.save)) }
            }
        )
    }) { pad ->
        if (!loaded) return@Scaffold
        Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            if (info != null) {
                OutlinedTextField(value = article, onValueChange = { article = it }, label = { Text(stringResource(R.string.article)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(value = qty, onValueChange = { qty = it }, label = { Text(stringResource(R.string.quantity)) }, singleLine = true, modifier = Modifier.weight(1f))
                    OutlinedTextField(value = unit, onValueChange = { unit = it }, label = { Text(stringResource(R.string.unit)) }, placeholder = { Text("kg, l, Stk …") }, singleLine = true, modifier = Modifier.weight(1f))
                }
                Picker(stringResource(if (isCheck) R.string.category else R.string.department), section, listOf("") + info.sections,
                    { if (it.isEmpty()) ctx.getString(if (isCheck) R.string.no_category else R.string.no_department) else it }, { section = it })
            } else OutlinedTextField(value = title, onValueChange = { title = it }, label = { Text(stringResource(R.string.title)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
            if (info == null) DetectedChip(title) { r ->
                title = r.title
                if (r.date != null) { hasDue = true; dueDate = r.date; withTime = r.time != null; r.time?.let { dueTime = it } }
            }
            // Aufgaben gehoeren zu einer Liste, nicht zu einem Kalender
            if (calendars.size > 1) Picker(stringResource(R.string.list_label), calendar, calendars, { it?.name ?: "" }, { calendar = it })
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.completed), Modifier.weight(1f))
                Switch(checked = completed, onCheckedChange = { completed = it })
            }
            if (info == null) Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.due), Modifier.weight(1f))
                Switch(checked = hasDue, onCheckedChange = { hasDue = it })
            }
            if (info == null && hasDue) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.all_day), Modifier.weight(1f))
                    Switch(checked = !withTime, onCheckedChange = { withTime = !it })
                }
                DateTimeRow(dueDate, dueTime, !withTime, onDate = { dueDate = it }, onTime = { dueTime = it })
                Picker(stringResource(R.string.reminder), reminder, REMINDER_OPTIONS, { Fmt.reminderLabel(ctx, it) }, { reminder = it })
            }
            if (info == null) Picker(stringResource(R.string.priority), if (priority in priorities) priority else 5, priorities, { Fmt.priorityLabel(ctx, it) }, { priority = it })
            OutlinedTextField(value = notes, onValueChange = { notes = it }, label = { Text(stringResource(R.string.notes)) }, minLines = 3, modifier = Modifier.fillMaxWidth())
            error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            if (existing != null && existing?.etag == null) Text(stringResource(R.string.offline_pending), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Button(onClick = { save() }, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.save)) }
        }
    }

    if (confirmDelete) AlertDialog(
        onDismissRequest = { confirmDelete = false },
        title = { Text(stringResource(R.string.delete)) },
        text = { Text(stringResource(R.string.delete_confirm, title)) },
        confirmButton = { TextButton(onClick = { confirmDelete = false; scope.launch { existing?.let { app.repo.delete(it.href) }; onClose() } }) { Text(stringResource(R.string.delete)) } },
        dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text(stringResource(R.string.cancel)) } }
    )
}
