package at.dimes.dmoffice.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Checkbox
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExposedDropdownMenuBox
import androidx.compose.material3.ExposedDropdownMenuDefaults
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MenuAnchorType
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.text.style.BaselineShift
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.outlined.Place
import androidx.compose.material.icons.outlined.TaskAlt
import java.time.Instant
import java.time.format.TextStyle
import java.time.temporal.WeekFields
import java.util.Locale
import androidx.compose.material.icons.filled.AutoAwesome
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.ItemEntity
import at.dimes.dmoffice.data.Occurrence
import java.time.LocalDate
import java.time.ZoneId

fun colorOf(c: Int?): Color = if (c != null) Color(c) else Color(DEFAULT_ACCENT.toInt())

/** Zeit-Kasten links wie bei Business Calendar: "24H" fuer ganztaegig, sonst Stunde gross und Minuten hochgestellt. */
@Composable
fun TimeBadge(o: Occurrence, day: LocalDate?, modifier: Modifier = Modifier) {
    val ctx = LocalContext.current
    val zone = ZoneId.systemDefault()
    val color = colorOf(o.color)
    val onColor = if (color.luminance() > 0.6f) Color.Black else Color.White
    val showStart = day == null || o.startDate(zone) == day
    val text: androidx.compose.ui.text.AnnotatedString = when {
        o.allDay || !showStart -> buildAnnotatedString { append("24"); withStyle(SpanStyle(fontSize = 9.sp, baselineShift = BaselineShift.Superscript)) { append("H") } }
        else -> {
            val t = Instant.ofEpochMilli(o.start).atZone(zone).toLocalTime()
            val is24 = android.text.format.DateFormat.is24HourFormat(ctx)
            val h = if (is24) t.hour else ((t.hour + 11) % 12 + 1)
            buildAnnotatedString {
                append(h.toString().padStart(2, '0'))
                withStyle(SpanStyle(fontSize = 9.sp, baselineShift = BaselineShift.Superscript)) { append("%02d".format(t.minute)) }
            }
        }
    }
    Box(
        modifier.size(44.dp).clip(RoundedCornerShape(4.dp)).background(if (o.isTask && o.item.completed) color.copy(alpha = 0.4f) else color),
        contentAlignment = Alignment.Center
    ) {
        if (o.isTask) Icon(Icons.Outlined.TaskAlt, contentDescription = null, tint = onColor, modifier = Modifier.size(22.dp))
        else Text(text, color = onColor, fontSize = 17.sp, fontWeight = FontWeight.Medium, lineHeight = 18.sp, maxLines = 1, softWrap = false)
    }
}

/** Eine Zeile in Agenda, Tag und Woche: Zeit-Kasten, fetter Titel, darunter Zeitraum und Ort; Aufgaben mit Haken. */
@Composable
fun OccurrenceRow(o: Occurrence, onClick: () -> Unit, onToggle: ((Boolean) -> Unit)? = null, showDate: Boolean = false, day: LocalDate? = null) {
    val ctx = LocalContext.current
    val zone = ZoneId.systemDefault()
    Row(
        modifier = Modifier.fillMaxWidth().clickable(onClick = onClick).padding(start = 12.dp, end = 8.dp, top = 3.dp, bottom = 3.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        TimeBadge(o, day)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(
                o.item.summary.ifBlank { "–" }, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold,
                maxLines = 1, overflow = TextOverflow.Ellipsis,
                textDecoration = if (o.isTask && o.item.completed) TextDecoration.LineThrough else null
            )
            val time = when {
                o.isTask -> stringResource(R.string.due) + ": " + (if (o.allDay) stringResource(R.string.all_day).lowercase() else Fmt.time(ctx, o.start))
                o.allDay && o.endDate(zone) > o.startDate(zone) -> Fmt.shortDate(o.startDate(zone)) + " – " + Fmt.shortDate(o.endDate(zone))
                o.allDay -> stringResource(R.string.all_day)
                else -> Fmt.timeRange(ctx, o.start, o.end, false)
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    (if (showDate) Fmt.shortDate(o.startDate(zone)) + " · " else "") + time,
                    style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis
                )
                o.item.location?.let { loc ->
                    Icon(Icons.Outlined.Place, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(start = 6.dp, end = 1.dp).size(14.dp))
                    Text(loc, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
        }
        if (o.isTask && onToggle != null) Checkbox(checked = o.item.completed, onCheckedChange = onToggle)
    }
}

/** Tageskopf wie bei Business Calendar: Datum gross, Wochentag und KW klein, Plus fuer einen neuen Termin an dem Tag. */
@Composable
fun DayHeader(day: LocalDate, today: LocalDate, onAdd: ((LocalDate) -> Unit)? = null) {
    val ctx = LocalContext.current
    val isToday = day == today
    val main = when (day) {
        today -> ctx.getString(R.string.today)
        today.plusDays(1) -> ctx.getString(R.string.tomorrow)
        else -> java.time.format.DateTimeFormatter.ofPattern("d. MMM", Locale.getDefault()).format(day)
    }
    val sub = (if (isToday || day == today.plusDays(1)) java.time.format.DateTimeFormatter.ofPattern("EEEE, d. MMM", Locale.getDefault()).format(day) else day.dayOfWeek.getDisplayName(TextStyle.FULL, Locale.getDefault())).uppercase() +
        ",  " + ctx.getString(R.string.week_number, day.get(WeekFields.ISO.weekOfWeekBasedYear()))
    Row(Modifier.fillMaxWidth().padding(start = 12.dp, end = 4.dp, top = 10.dp, bottom = 2.dp), verticalAlignment = Alignment.Bottom) {
        Text(main, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold,
            color = if (isToday) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface)
        Spacer(Modifier.width(8.dp))
        Text(sub, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f).padding(bottom = 5.dp), maxLines = 1, overflow = TextOverflow.Ellipsis)
        if (onAdd != null) IconButton(onClick = { onAdd(day) }, modifier = Modifier.size(32.dp)) {
            Icon(Icons.Default.Add, contentDescription = stringResource(R.string.new_event), tint = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
fun TaskRow(t: ItemEntity, color: Int?, onClick: () -> Unit, onToggle: (Boolean) -> Unit) {
    val ctx = LocalContext.current
    val now = System.currentTimeMillis()
    Row(modifier = Modifier.fillMaxWidth().clickable(onClick = onClick).padding(start = 4.dp, end = 16.dp, top = 2.dp, bottom = 2.dp), verticalAlignment = Alignment.CenterVertically) {
        Checkbox(checked = t.completed, onCheckedChange = onToggle)
        Column(Modifier.weight(1f)) {
            Text(
                t.summary.ifBlank { "–" }, style = MaterialTheme.typography.bodyLarge, maxLines = 2, overflow = TextOverflow.Ellipsis,
                textDecoration = if (t.completed) TextDecoration.LineThrough else null,
                color = if (t.completed) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface
            )
            val due = Fmt.dueLabel(ctx, t.due, t.allDay)
            if (due != null || t.priority > 0) Row {
                if (due != null) Text(due, style = MaterialTheme.typography.bodySmall,
                    color = if (!t.completed && t.due!! < now) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
                if (t.priority in 1..4) Text(if (due != null) "  · " + stringResource(R.string.priority_high) else stringResource(R.string.priority_high),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error, fontWeight = FontWeight.Medium)
            }
        }
        Box(Modifier.width(4.dp).height(28.dp).clip(RoundedCornerShape(2.dp)).background(colorOf(color)))
    }
}

@Composable
fun SectionHeader(text: String, highlight: Boolean = false) {
    Text(
        text, style = MaterialTheme.typography.labelLarge,
        color = if (highlight) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 16.dp, top = 14.dp, bottom = 4.dp)
    )
}

/** Auswahlfeld im Formular. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun <T> Picker(label: String, value: T, options: List<T>, text: (T) -> String, onSelect: (T) -> Unit, modifier: Modifier = Modifier) {
    var open by remember { mutableStateOf(false) }
    ExposedDropdownMenuBox(expanded = open, onExpandedChange = { open = it }, modifier = modifier) {
        OutlinedTextField(
            value = text(value), onValueChange = {}, readOnly = true, label = { Text(label) }, singleLine = true,
            trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = open) },
            modifier = Modifier.menuAnchor(MenuAnchorType.PrimaryNotEditable).fillMaxWidth()
        )
        ExposedDropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            for (o in options) DropdownMenuItem(text = { Text(text(o)) }, onClick = { onSelect(o); open = false })
        }
    }
}

/** Vorschlag aus der Texterkennung unter dem Titelfeld; Tipp uebernimmt Datum/Zeit und kuerzt den Titel. */
@Composable
fun DetectedChip(title: String, onApply: (at.dimes.dmoffice.data.NaturalDate.Result) -> Unit) {
    val ctx = LocalContext.current
    val r = remember(title) { if (title.isBlank()) null else at.dimes.dmoffice.data.NaturalDate.parse(title) }
    if (r == null || !r.found || r.title == title.trim()) return
    androidx.compose.material3.AssistChip(
        onClick = { onApply(r) },
        label = { Text(stringResource(R.string.detected_apply, Fmt.detected(ctx, r)), maxLines = 2) },
        leadingIcon = { androidx.compose.material3.Icon(androidx.compose.material.icons.Icons.Default.AutoAwesome, contentDescription = null, modifier = Modifier.height(18.dp)) }
    )
}

fun LocalDate.epochMillis(zone: ZoneId = ZoneId.systemDefault()): Long = atStartOfDay(zone).toInstant().toEpochMilli()

/** Farbige Kategorien einer Mail wie im Web: kleine Schilder in der Farbe der Kategorie. */
@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
fun CategoryChips(list: List<at.dimes.dmoffice.api.MailCategory>) {
    if (list.isEmpty()) return
    androidx.compose.foundation.layout.FlowRow(Modifier.padding(top = 3.dp), horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(4.dp)) {
        for (c in list) {
            val col = parseColor(c.color)
            Text(c.name, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurface, maxLines = 1,
                modifier = Modifier.clip(androidx.compose.foundation.shape.RoundedCornerShape(4.dp)).background(col.copy(alpha = 0.28f)).padding(horizontal = 6.dp, vertical = 1.dp))
        }
    }
}
