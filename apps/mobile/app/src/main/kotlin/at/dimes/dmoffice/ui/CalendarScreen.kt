package at.dimes.dmoffice.ui

import android.content.Context
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.width
import androidx.compose.material3.FilterChip
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextDecoration
import java.time.Instant
import java.time.LocalTime
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Today
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.Occurrence
import kotlinx.coroutines.launch
import java.time.DayOfWeek
import java.time.LocalDate
import java.time.ZoneId
import java.time.temporal.TemporalAdjusters
import java.time.temporal.WeekFields

/** Ansichten wie im Web (Tag, Arbeitswoche, Woche, Monat, Liste) und die Agenda (fortlaufend ab heute). */
enum class CalMode(val label: Int) {
    Agenda(R.string.agenda), Day(R.string.day), WorkWeek(R.string.work_week), Week(R.string.week), Month(R.string.month), List(R.string.list_view)
}

/** Zuletzt gewaehlte Kalenderansicht, bleibt ueber Neustarts erhalten. */
object CalendarPrefs {
    private fun sp(ctx: Context) = ctx.getSharedPreferences("ui", Context.MODE_PRIVATE)
    val mode = mutableStateOf<CalMode?>(null)
    fun get(ctx: Context): CalMode = mode.value
        ?: (try { CalMode.valueOf(sp(ctx).getString("calMode", null) ?: "") } catch (_: Exception) { CalMode.Week }).also { mode.value = it }
    fun setMode(ctx: Context, m: CalMode) { mode.value = m; sp(ctx).edit().putString("calMode", m.name).apply() }
}

/** Kalender mit Agenda, Tag, Arbeitswoche, Woche, Monat und Liste. Die Auswahl des Tages bleibt beim Umschalten erhalten. */
@Composable
fun CalendarScreen(day: LocalDate, onDay: (LocalDate) -> Unit, onOpen: (String) -> Unit, onNewEventAt: (Long) -> Unit = {}, onNewEvent: (LocalDate) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val zone = ZoneId.systemDefault()
    CalendarPrefs.get(ctx)
    val mode = CalendarPrefs.mode.value ?: CalMode.Week
    val weekStart = DayOfWeek.MONDAY
    val today = LocalDate.now(zone)

    // Geladener Bereich je nach Ansicht
    val monday = day.with(TemporalAdjusters.previousOrSame(weekStart))
    val (from, to) = when (mode) {
        CalMode.Agenda -> today to today.plusDays(1)
        CalMode.Day -> day to day.plusDays(1)
        CalMode.WorkWeek, CalMode.Week -> monday to monday.plusWeeks(1)
        CalMode.Month -> day.withDayOfMonth(1).with(TemporalAdjusters.previousOrSame(weekStart)).let { it to it.plusWeeks(6) }
        CalMode.List -> day.withDayOfMonth(1).let { it to it.plusMonths(1) }
    }
    val flow = remember(from, to) { app.repo.occurrences(from.epochMillis(zone), to.epochMillis(zone)) }
    val occ by flow.collectAsState(initial = emptyList())
    val settings by app.prefs.settings.collectAsState()
    val shown = remember(occ, settings) { occ.filter { (settings.showCompleted || !(it.isTask && it.item.completed)) && (settings.agendaShowTasks || !it.isTask) } }

    fun byDay(d: LocalDate): List<Occurrence> = shown.filter { o -> o.startDate(zone) <= d && o.endDate(zone) >= d }
    fun step(n: Long) = onDay(when (mode) {
        CalMode.Day -> day.plusDays(n)
        CalMode.WorkWeek, CalMode.Week -> day.plusWeeks(n)
        else -> day.plusMonths(n)
    })
    val toggle: (String, Boolean) -> Unit = { h, d -> scope.launch { app.repo.setCompleted(h, d) } }
    val openDay: (LocalDate) -> Unit = { onDay(it); CalendarPrefs.setMode(ctx, CalMode.Day) }

    Column(Modifier.fillMaxSize()) {
        // Ansichten als Chips (sechs passen nicht in eine Segmentleiste)
        Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            for (m in CalMode.entries) FilterChip(selected = mode == m, onClick = { CalendarPrefs.setMode(ctx, m) }, label = { Text(stringResource(m.label)) })
        }
        if (mode != CalMode.Agenda) Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { step(-1) }) { Icon(Icons.AutoMirrored.Filled.KeyboardArrowLeft, contentDescription = null) }
            Text(
                when (mode) {
                    CalMode.Day -> Fmt.longDate(day)
                    CalMode.WorkWeek, CalMode.Week -> Fmt.monthYear(monday.plusDays(3)) + " · " + stringResource(R.string.week_number, monday.get(WeekFields.ISO.weekOfWeekBasedYear()))
                    else -> Fmt.monthYear(day)
                },
                modifier = Modifier.weight(1f), textAlign = TextAlign.Center, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis
            )
            IconButton(onClick = { step(1) }) { Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = null) }
            IconButton(onClick = { onDay(today) }) { Icon(Icons.Default.Today, contentDescription = stringResource(R.string.today)) }
        } else Spacer(Modifier.height(4.dp))
        // Nach links/rechts wischen: vor bzw. zurueck (nicht in der fortlaufenden Agenda)
        var drag by remember { mutableStateOf(0f) }
        val swipe = if (mode == CalMode.Agenda) Modifier else Modifier.pointerInput(mode, day) {
            detectHorizontalDragGestures(
                onDragStart = { drag = 0f },
                onDragEnd = { if (kotlin.math.abs(drag) > 80.dp.toPx()) step(if (drag < 0) 1 else -1); drag = 0f },
                onDragCancel = { drag = 0f },
            ) { change, dx -> drag += dx; change.consume() }
        }
        Box(Modifier.fillMaxSize().then(swipe)) { when (mode) {
            CalMode.Agenda -> AgendaScreen(onOpen, onNewEvent)
            CalMode.Day -> TimeGrid(listOf(day), today, ::byDay, {}, onOpen, toggle, onNewEventAt)
            CalMode.WorkWeek -> TimeGrid((0..4).map { monday.plusDays(it.toLong()) }, today, ::byDay, openDay, onOpen, toggle, onNewEventAt)
            CalMode.Week -> TimeGrid((0..6).map { monday.plusDays(it.toLong()) }, today, ::byDay, openDay, onOpen, toggle, onNewEventAt)
            CalMode.Month -> MonthView(day, from, today, ::byDay, onDay, onOpen, toggle)
            CalMode.List -> MonthList(from, to, today, ::byDay, onOpen, toggle, onNewEvent)
        } }
    }
}

/** Liste: alle Tage des Monats mit Eintraegen, wie die Ansicht "Liste" im Web. */
@Composable
private fun MonthList(from: LocalDate, to: LocalDate, today: LocalDate, byDay: (LocalDate) -> List<Occurrence>, onOpen: (String) -> Unit, onToggle: (String, Boolean) -> Unit, onNewEvent: (LocalDate) -> Unit) {
    val days = generateSequence(from) { it.plusDays(1) }.takeWhile { it < to }.map { it to byDay(it) }.filter { it.second.isNotEmpty() }.toList()
    if (days.isEmpty()) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { Text(stringResource(R.string.no_events), color = MaterialTheme.colorScheme.onSurfaceVariant) }
        return
    }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
        for ((d, list) in days) {
            item(key = "h$d") { DayHeader(d, today, onNewEvent) }
            items(list.size, key = { i -> "$d/${list[i].item.href}/${list[i].start}" }) { i ->
                val o = list[i]
                OccurrenceRow(o, onClick = { onOpen(o.item.href) }, onToggle = if (o.isTask) { x -> onToggle(o.item.href, x) } else null, day = d)
            }
        }
    }
}

/**
 * Zeitraster fuer Tag, Arbeitswoche und Woche wie im Web: links die Stunden, Termine als Bloecke in ihrer
 * Laenge (Ueberschneidungen nebeneinander), ganztaegige Termine und Aufgaben ohne Uhrzeit oben, rote Linie
 * fuer jetzt. Tippen in eine freie Stunde legt dort einen Termin an, Tippen auf einen Tag im Kopf zeigt ihn als Tag.
 */
@Composable
private fun TimeGrid(days: List<LocalDate>, today: LocalDate, byDay: (LocalDate) -> List<Occurrence>, onDayClick: (LocalDate) -> Unit,
                     onOpen: (String) -> Unit, onToggle: (String, Boolean) -> Unit, onNewAt: (Long) -> Unit) {
    val ctx = LocalContext.current
    val zone = ZoneId.systemDefault()
    val hourH = if (days.size == 1) 60.dp else 52.dp
    val gutter = 40.dp
    val scroll = rememberScrollState()
    val density = LocalDensity.current
    // Beim Oeffnen ab 7 Uhr, heute eine Stunde vor jetzt
    LaunchedEffect(days.first()) {
        val h = if (today in days) (LocalTime.now().hour - 1).coerceIn(0, 18) else 7
        scroll.scrollTo(with(density) { (hourH * h).roundToPx() })
    }
    val line = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f)
    fun untimed(o: Occurrence) = o.allDay || (o.isTask && Instant.ofEpochMilli(o.start).atZone(zone).toLocalTime() == LocalTime.MIDNIGHT && o.end <= o.start)

    Column(Modifier.fillMaxSize()) {
        // Kopf: Wochentag und Datum je Spalte
        Row(Modifier.fillMaxWidth().padding(end = 2.dp)) {
            Spacer(Modifier.width(gutter))
            for (d in days) Column(Modifier.weight(1f).clickable { onDayClick(d) }.padding(vertical = 4.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Text(Fmt.dayName(d), style = MaterialTheme.typography.labelSmall, color = if (d == today) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                Box(Modifier.size(30.dp).clip(CircleShape).background(if (d == today) MaterialTheme.colorScheme.primary else Color.Transparent), contentAlignment = Alignment.Center) {
                    Text(d.dayOfMonth.toString(), color = if (d == today) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurface, fontWeight = FontWeight.Medium)
                }
            }
        }
        // Ganztaegig und Aufgaben ohne Uhrzeit
        val allDay = days.map { d -> byDay(d).filter { untimed(it) } }
        if (allDay.any { it.isNotEmpty() }) {
            Row(Modifier.fillMaxWidth().padding(end = 2.dp, bottom = 2.dp)) {
                Spacer(Modifier.width(gutter))
                for (list in allDay) Column(Modifier.weight(1f).padding(horizontal = 1.dp)) {
                    for (o in list.take(3)) Text(
                        o.item.summary.ifBlank { "–" }, maxLines = 1, overflow = TextOverflow.Ellipsis, fontSize = 11.sp, color = Color.White,
                        textDecoration = if (o.isTask && o.item.completed) TextDecoration.LineThrough else null,
                        modifier = Modifier.fillMaxWidth().padding(vertical = 1.dp).clip(RoundedCornerShape(4.dp)).background(colorOf(o.color))
                            .clickable { onOpen(o.item.href) }.padding(horizontal = 4.dp, vertical = 2.dp)
                    )
                    if (list.size > 3) Text("+${list.size - 3}", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(start = 4.dp))
                }
            }
        }
        HorizontalDivider()
        // Stundenraster
        Row(Modifier.fillMaxSize().verticalScroll(scroll).padding(end = 2.dp, bottom = 88.dp)) {
            Column(Modifier.width(gutter)) {
                for (h in 0..23) Box(Modifier.height(hourH).fillMaxWidth(), contentAlignment = Alignment.TopEnd) {
                    if (h > 0) Text("%02d".format(h), fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(end = 6.dp).offset(y = (-7).dp))
                }
            }
            for ((i, d) in days.withIndex()) {
                val dayStart = d.atStartOfDay(zone).toInstant().toEpochMilli()
                val dayEnd = d.plusDays(1).atStartOfDay(zone).toInstant().toEpochMilli()
                val timed = byDay(d).filter { it !in allDay[i] }
                val blocks = layoutBlocks(timed.map { o ->
                    val s0 = maxOf(o.start, dayStart)
                    val e0 = minOf(if (o.end > o.start) o.end else o.start + 30 * 60_000, dayEnd)
                    Triple(o, s0, maxOf(e0, s0 + 20 * 60_000))
                })
                BoxWithConstraints(
                    Modifier.weight(1f).height(hourH * 24)
                        .background(if (d == today) MaterialTheme.colorScheme.primary.copy(alpha = 0.04f) else Color.Transparent)
                        .pointerInput(d) {
                            detectTapGestures { pos ->
                                val hour = (pos.y / hourH.toPx()).toInt().coerceIn(0, 23)
                                onNewAt(d.atTime(hour, 0).atZone(zone).toInstant().toEpochMilli())
                            }
                        }
                ) {
                    val colW = maxWidth
                    for (h in 0..23) Box(Modifier.offset(y = hourH * h).fillMaxWidth().height(1.dp).background(line))
                    Box(Modifier.width(1.dp).fillMaxSize().background(line))
                    for (b in blocks) {
                        val o = b.o
                        val top = hourH * ((b.start - dayStart) / 3_600_000f)
                        val height = hourH * ((b.end - b.start) / 3_600_000f)
                        val w = colW / b.lanes
                        Column(
                            Modifier.offset(x = w * b.lane, y = top).width(w).height(height).padding(1.dp)
                                .clip(RoundedCornerShape(4.dp)).background(colorOf(o.color).copy(alpha = if (o.isTask && o.item.completed) 0.45f else 0.9f))
                                .clickable { onOpen(o.item.href) }.padding(horizontal = 3.dp, vertical = 1.dp)
                        ) {
                            Text(o.item.summary.ifBlank { "–" }, color = Color.White, fontSize = 11.sp, lineHeight = 12.sp, fontWeight = FontWeight.Medium,
                                maxLines = if (height < 36.dp) 1 else 3, overflow = TextOverflow.Ellipsis)
                            if (height >= 36.dp) Text(Fmt.time(ctx, o.start), color = Color.White.copy(alpha = 0.85f), fontSize = 10.sp, lineHeight = 11.sp, maxLines = 1)
                        }
                    }
                    // Jetzt-Linie
                    if (d == today) {
                        val now = System.currentTimeMillis()
                        Box(Modifier.offset(y = hourH * ((now - dayStart) / 3_600_000f)).fillMaxWidth().height(2.dp).background(Color(0xFFE53935)))
                    }
                }
            }
        }
    }
}

private class Block(val o: Occurrence, val start: Long, val end: Long, var lane: Int = 0, var lanes: Int = 1)

/** Ueberschneidende Termine nebeneinander: je Gruppe so viele Spuren wie gleichzeitig noetig. */
private fun layoutBlocks(items: List<Triple<Occurrence, Long, Long>>): List<Block> {
    val blocks = items.map { Block(it.first, it.second, it.third) }.sortedWith(compareBy<Block>({ it.start }, { -it.end }))
    var group = mutableListOf<Block>()
    var groupEnd = Long.MIN_VALUE
    val laneEnds = mutableListOf<Long>()
    fun close() {
        val n = laneEnds.size.coerceAtLeast(1)
        group.forEach { it.lanes = n }
        group = mutableListOf(); laneEnds.clear(); groupEnd = Long.MIN_VALUE
    }
    for (b in blocks) {
        if (group.isNotEmpty() && b.start >= groupEnd) close()
        val free = laneEnds.indexOfFirst { it <= b.start }
        if (free >= 0) { b.lane = free; laneEnds[free] = b.end } else { b.lane = laneEnds.size; laneEnds.add(b.end) }
        group.add(b)
        groupEnd = maxOf(groupEnd, b.end)
    }
    close()
    return blocks
}

@Composable
private fun DayList(day: LocalDate, list: List<Occurrence>, onOpen: (String) -> Unit, onToggle: (String, Boolean) -> Unit) {
    if (list.isEmpty()) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { Text(stringResource(R.string.no_events), color = MaterialTheme.colorScheme.onSurfaceVariant) }
        return
    }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
        items(list.size, key = { i -> list[i].item.href + list[i].start }) { i ->
            val o = list[i]
            OccurrenceRow(o, onClick = { onOpen(o.item.href) }, onToggle = if (o.isTask) { d -> onToggle(o.item.href, d) } else null, day = day)
        }
    }
}

/** Monat: sechs Wochen als Raster mit bis zu drei Eintraegen je Tag, darunter die Liste des gewaehlten Tages. */
@Composable
private fun MonthView(month: LocalDate, gridStart: LocalDate, today: LocalDate, byDay: (LocalDate) -> List<Occurrence>, onDay: (LocalDate) -> Unit, onOpen: (String) -> Unit, onToggle: (String, Boolean) -> Unit) {
    val scroll = rememberScrollState()
    Column(Modifier.fillMaxSize().verticalScroll(scroll)) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp)) {
            for (i in 0..6) Text(
                Fmt.dayName(gridStart.plusDays(i.toLong())), modifier = Modifier.weight(1f), textAlign = TextAlign.Center,
                style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }
        for (w in 0 until 6) {
            Row(Modifier.fillMaxWidth().heightIn(min = 72.dp).padding(horizontal = 4.dp)) {
                for (i in 0..6) {
                    val d = gridStart.plusDays((w * 7 + i).toLong())
                    val inMonth = d.month == month.month
                    val list = byDay(d)
                    Column(
                        Modifier.weight(1f).fillMaxSize().padding(1.dp).clip(RoundedCornerShape(6.dp))
                            .background(if (d == month) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surface)
                            .clickable { onDay(d) }.padding(2.dp)
                    ) {
                        Box(Modifier.size(22.dp).clip(CircleShape).background(if (d == today) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surface.copy(alpha = 0f)), contentAlignment = Alignment.Center) {
                            Text(d.dayOfMonth.toString(), fontSize = 12.sp,
                                color = when { d == today -> MaterialTheme.colorScheme.onPrimary; inMonth -> MaterialTheme.colorScheme.onSurface; else -> MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f) })
                        }
                        for (o in list.take(3)) Text(
                            o.item.summary.ifBlank { "–" }, fontSize = 9.sp, lineHeight = 11.sp, maxLines = 1, overflow = TextOverflow.Clip,
                            color = if (o.isTask && o.item.completed) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                            modifier = Modifier.fillMaxWidth().padding(top = 1.dp).clip(RoundedCornerShape(2.dp))
                                .background(colorOf(o.color).copy(alpha = if (inMonth) 0.30f else 0.12f)).padding(horizontal = 2.dp)
                        )
                        if (list.size > 3) Text(stringResource(R.string.more_items, list.size - 3), fontSize = 9.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }
        HorizontalDivider(Modifier.padding(top = 4.dp))
        DayHeader(month, today, null)
        val list = byDay(month)
        if (list.isEmpty()) Text(stringResource(R.string.no_events), modifier = Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
        for (o in list) OccurrenceRow(o, onClick = { onOpen(o.item.href) }, onToggle = if (o.isTask) { x -> onToggle(o.item.href, x) } else null, day = month)
        Spacer(Modifier.height(88.dp))
    }
}
