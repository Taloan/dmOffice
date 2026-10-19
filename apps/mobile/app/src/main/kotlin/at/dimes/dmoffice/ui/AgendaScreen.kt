package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.MultiDayRule
import at.dimes.dmoffice.data.Occurrence
import kotlinx.coroutines.launch
import java.time.LocalDate
import java.time.ZoneId

/** Agenda: die naechsten 90 Tage, nach Tagen gruppiert, laufende Termine von heute inklusive. */
@Composable
fun AgendaScreen(onOpen: (String) -> Unit, onNewEvent: (LocalDate) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val zone = ZoneId.systemDefault()
    val today = remember { LocalDate.now(zone) }
    val settings by app.prefs.settings.collectAsState()
    val from = remember { today.epochMillis(zone) }
    val last = remember(settings.agendaDays) { today.plusDays(settings.agendaDays.toLong() - 1) }
    val to = remember(last) { last.plusDays(1).epochMillis(zone) }
    val flow = remember(to) { app.repo.occurrences(from, to) }
    val occ by flow.collectAsState(initial = emptyList())

    val grouped = remember(occ, settings) {
        val map = linkedMapOf<LocalDate, MutableList<Occurrence>>()
        val shown = occ.filter { (settings.showCompleted || !(it.isTask && it.item.completed)) && (settings.agendaShowTasks || !it.isTask) }
        for (o in shown) for (d in MultiDayRule.days(o, zone, settings.agendaMultiDay, today, last, today)) map.getOrPut(d) { mutableListOf() }.add(o)
        map.toSortedMap()
    }

    if (grouped.isEmpty()) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(stringResource(R.string.nothing_planned), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        return
    }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
        for ((day, list) in grouped) {
            item(key = "h$day") { DayHeader(day, today, onNewEvent) }
            items(list.size, key = { i -> "$day/${list[i].item.href}/${list[i].start}" }) { i ->
                val o = list[i]
                OccurrenceRow(o, onClick = { onOpen(o.item.href) }, onToggle = if (o.isTask) { done -> scope.launch { app.repo.setCompleted(o.item.href, done) } } else null, day = day)
            }
        }
    }
}
