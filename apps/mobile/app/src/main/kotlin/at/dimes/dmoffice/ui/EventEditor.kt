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
import at.dimes.dmoffice.data.KIND_TASK
import at.dimes.dmoffice.ical.Ical
import at.dimes.dmoffice.ical.IcalTime
import at.dimes.dmoffice.ical.RRule
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId

/** Oeffnet je nach Art des Objekts den Termin- oder Aufgaben-Editor. */
@Composable
fun ItemRouter(href: String, onClose: () -> Unit) {
    val app = App.get(LocalContext.current)
    var item by remember { mutableStateOf<ItemEntity?>(null) }
    var missing by remember { mutableStateOf(false) }
    LaunchedEffect(href) { item = app.repo.item(href); if (item == null) missing = true }
    LaunchedEffect(missing) { if (missing) onClose() }
    item?.let { if (it.kind == KIND_TASK) TaskEditor(href, onClose) else EventEditor(href, 0L, onClose) }
}

val REMINDER_OPTIONS: List<Long?> = listOf(null, 0L, 5 * 60L, 10 * 60L, 15 * 60L, 30 * 60L, 3600L, 2 * 3600L, 86400L, 2 * 86400L, 7 * 86400L)

private enum class Repeat(val label: Int, val rule: String?) {
    None(R.string.repeat_none, null), Daily(R.string.repeat_daily, "FREQ=DAILY"), Weekly(R.string.repeat_weekly, "FREQ=WEEKLY"),
    Monthly(R.string.repeat_monthly, "FREQ=MONTHLY"), Yearly(R.string.repeat_yearly, "FREQ=YEARLY"), Custom(R.string.repeat_custom, "")
}

/** Termin anlegen oder bearbeiten. Bei Serien gilt die Aenderung fuer die ganze Serie. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun EventEditor(href: String?, startMs: Long, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val zone = ZoneId.systemDefault()
    val settings = app.prefs.settings.value

    var loaded by remember { mutableStateOf(href == null) }
    var existing by remember { mutableStateOf<ItemEntity?>(null) }
    var calendars by remember { mutableStateOf<List<CalendarEntity>>(emptyList()) }
    var calendar by remember { mutableStateOf<CalendarEntity?>(null) }
    var title by remember { mutableStateOf("") }
    var location by remember { mutableStateOf("") }
    var notes by remember { mutableStateOf("") }
    var allDay by remember { mutableStateOf(false) }
    val initial = remember {
        // Aus dem Zeitraster mit Uhrzeit, sonst der Tag zur naechsten vollen Stunde
        val at = if (startMs > 0) Instant.ofEpochMilli(startMs).atZone(zone).toLocalDateTime() else null
        val base = when {
            at == null -> LocalDateTime.now().plusHours(1).withMinute(0)
            at.toLocalTime() != LocalTime.MIDNIGHT -> at
            else -> at.toLocalDate().atTime(minOf(LocalTime.now().hour + 1, 23), 0)
        }
        base.withSecond(0).withNano(0)
    }
    var startDate by remember { mutableStateOf(initial.toLocalDate()) }
    var startTime by remember { mutableStateOf(initial.toLocalTime()) }
    var endDate by remember { mutableStateOf(initial.toLocalDate()) }
    var endTime by remember { mutableStateOf(initial.toLocalTime().plusHours(1)) }
    var reminder by remember { mutableStateOf<Long?>(if (settings.defaultReminderMinutes < 0) null else settings.defaultReminderMinutes * 60L) }
    var repeat by remember { mutableStateOf(Repeat.None) }
    var customRule by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var confirmDelete by remember { mutableStateOf(false) }

    LaunchedEffect(href) {
        val cals = app.repo.calendarList()
        calendars = cals.filter { it.supportsEvents }.ifEmpty { cals }
        if (href != null) {
            val it = app.repo.item(href)
            if (it == null) { onClose(); return@LaunchedEffect }
            existing = it
            calendar = cals.firstOrNull { c -> c.href == it.calendarHref }
            title = it.summary; location = it.location ?: ""; notes = it.description ?: ""
            allDay = it.allDay
            val cal = Items.parseCalendar(it.ics)
            val m = cal?.let { c -> Items.master(c) }
            val s = m?.let { c -> Ical.time(c.get("DTSTART"), zone) }
            if (s != null) {
                val sz = if (s.allDay) s.date!!.atStartOfDay() else LocalDateTime.ofInstant(s.instant, zone)
                startDate = sz.toLocalDate(); startTime = sz.toLocalTime()
                val durSec = at.dimes.dmoffice.ical.Recurrence.durationOf(m, zone)
                val e = if (s.allDay) s.date!!.plusDays(maxOf(1, durSec / 86400) - 1).atStartOfDay() else sz.plusSeconds(durSec)
                endDate = e.toLocalDate(); endTime = e.toLocalTime()
            }
            reminder = it.reminderSeconds
            val rr = m?.get("RRULE")?.value
            if (rr != null) {
                val r = RRule(rr)
                repeat = if (r.isSimple) when (r.freq) { "DAILY" -> Repeat.Daily; "WEEKLY" -> Repeat.Weekly; "MONTHLY" -> Repeat.Monthly; "YEARLY" -> Repeat.Yearly; else -> Repeat.Custom } else Repeat.Custom
                customRule = rr
            }
        } else calendar = calendars.firstOrNull { it.visible } ?: calendars.firstOrNull()
        loaded = true
    }

    fun save() {
        if (title.isBlank()) { error = ctx.getString(R.string.title_required); return }
        val cal = calendar ?: return
        val start: IcalTime
        val end: IcalTime
        if (allDay) {
            if (endDate < startDate) { error = ctx.getString(R.string.end_before_start); return }
            start = IcalTime(startDate, null, null); end = IcalTime(endDate.plusDays(1), null, null)
        } else {
            val s = startDate.atTime(startTime).atZone(zone)
            val e = endDate.atTime(endTime).atZone(zone)
            if (!e.isAfter(s)) { error = ctx.getString(R.string.end_before_start); return }
            start = IcalTime(null, s.toInstant(), zone); end = IcalTime(null, e.toInstant(), zone)
        }
        val rule = when (repeat) { Repeat.None -> null; Repeat.Custom -> customRule; else -> repeat.rule }
        scope.launch {
            app.repo.saveEvent(existing?.href, cal.href, Items.EventInput(title.trim(), location.trim().ifEmpty { null }, notes.trim().ifEmpty { null }, start, end, rule, reminder))
            onClose()
        }
    }

    Scaffold(topBar = {
        TopAppBar(
            title = { Text(stringResource(if (existing == null) R.string.new_event else R.string.edit_event)) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                if (existing != null) IconButton(onClick = { confirmDelete = true }) { Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.delete)) }
                TextButton(onClick = { save() }, enabled = loaded) { Text(stringResource(R.string.save)) }
            }
        )
    }) { pad ->
        if (!loaded) return@Scaffold
        Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedTextField(value = title, onValueChange = { title = it }, label = { Text(stringResource(R.string.title)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
            DetectedChip(title) { r ->
                title = r.title
                if (r.date != null) { startDate = r.date; endDate = r.date }
                if (r.time != null) {
                    allDay = false; startTime = r.time
                    val e = r.endTime ?: r.time.plusHours(1)
                    if (e.isBefore(r.time)) endDate = r.date!!.plusDays(1)
                    endTime = e
                } else allDay = true
            }
            if (calendars.size > 1) Picker(stringResource(R.string.calendar_label), calendar, calendars, { it?.name ?: "" }, { calendar = it })
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.all_day), Modifier.weight(1f))
                Switch(checked = allDay, onCheckedChange = { allDay = it })
            }
            Text(stringResource(R.string.start), style = MaterialTheme.typography.labelLarge)
            DateTimeRow(startDate, startTime, allDay, onDate = { d ->
                val shift = java.time.temporal.ChronoUnit.DAYS.between(startDate, d); startDate = d; endDate = endDate.plusDays(shift)
            }, onTime = { t ->
                val diff = java.time.Duration.between(startTime, t); startTime = t
                val e = endDate.atTime(endTime).plus(diff); endDate = e.toLocalDate(); endTime = e.toLocalTime()
            })
            Text(stringResource(R.string.end), style = MaterialTheme.typography.labelLarge)
            DateTimeRow(endDate, endTime, allDay, onDate = { endDate = it }, onTime = { endTime = it })
            Picker(stringResource(R.string.reminder), reminder, REMINDER_OPTIONS, { Fmt.reminderLabel(ctx, it) }, { reminder = it })
            Picker(stringResource(R.string.repeat), repeat, Repeat.entries.filter { it != Repeat.Custom || customRule != null },
                { if (it == Repeat.Custom) ctx.getString(R.string.repeat_custom, customRule ?: "") else ctx.getString(it.label) }, { repeat = it })
            if (existing?.recurring == true) Text(stringResource(R.string.series_note), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            OutlinedTextField(value = location, onValueChange = { location = it }, label = { Text(stringResource(R.string.location)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(value = notes, onValueChange = { notes = it }, label = { Text(stringResource(R.string.notes)) }, minLines = 3, modifier = Modifier.fillMaxWidth())
            error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            if (existing?.etag == null && existing != null) Text(stringResource(R.string.offline_pending), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
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
