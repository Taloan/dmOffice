package at.dimes.dmoffice.ui

import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.layout.WindowInsets
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.journal.Media
import at.dimes.dmoffice.journal.MediaHit
import at.dimes.dmoffice.journal.OnThisDay
import at.dimes.dmoffice.journal.Preview
import at.dimes.dmoffice.journal.moodEmoji
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/** Tagebuch (Version 2): Zeitleiste, Kalender, „Heute vor …“, Medien; Plus-Knopf für einen neuen Eintrag. */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
fun JournalScreen(onOpen: (journal: Long, uri: String) -> Unit, onNew: () -> Unit, onNewDay: (java.time.LocalDate) -> Unit = { onNew() }) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val search = rememberTabSearch()
    var view by rememberSaveable { mutableIntStateOf(0) }
    var list by remember { mutableStateOf<List<Preview>?>(app.journal.entriesCache) }
    var complete by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var exporting by remember { mutableStateOf(false) }
    if (exporting) JournalExportDialog(onDismiss = { exporting = false }, onError = { error = it })

    // Bereichssperre: nach dem Entsperren neu laden
    val unlocked by TresorGate.changed.collectAsState()
    LaunchedEffect(search.query, view, unlocked) {
        if (view != 0) return@LaunchedEffect
        if (search.query.isNotBlank()) kotlinx.coroutines.delay(300)
        try { list = app.journal.entries(q = search.query); complete = (list?.size ?: 0) < 100; error = null }
        catch (e: Exception) { error = e.message ?: e.toString(); if (list == null) list = emptyList() }
    }

    Scaffold(
        contentWindowInsets = WindowInsets(0),
        topBar = {
            TabTopBar(stringResource(R.string.journal), search = search, searchHint = stringResource(R.string.journal_search), insets = WindowInsets(0), actions = {
                var menu by remember { mutableStateOf(false) }
                Box {
                    androidx.compose.material3.IconButton(onClick = { menu = true }) { androidx.compose.material3.Icon(androidx.compose.material.icons.Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
                    androidx.compose.material3.DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        androidx.compose.material3.DropdownMenuItem(text = { Text(stringResource(R.string.journal_export)) }, onClick = { menu = false; exporting = true })
                    }
                }
            })
        },
        floatingActionButton = { AddButton(stringResource(R.string.journal_new), onClick = onNew) },
    ) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            PrimaryTabRow(selectedTabIndex = view) {
                listOf(R.string.journal_timeline, R.string.journal_calendar, R.string.journal_onthisday, R.string.journal_media).forEachIndexed { i, label ->
                    Tab(selected = view == i, onClick = { view = i }, text = { Text(stringResource(label), maxLines = 1, overflow = TextOverflow.Ellipsis) })
                }
            }
            error?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
            when (view) {
                0 -> Timeline(list, complete, onOpen, onMore = {
                    val last = list?.lastOrNull() ?: return@Timeline
                    scope.launch {
                        try { val more = app.journal.entries(q = search.query, before = last.date); list = list.orEmpty() + more; complete = more.size < 100 } catch (_: Exception) { }
                    }
                })
                1 -> MonthCalendar(onOpen, onNewDay)
                2 -> OnThisDayList(onOpen)
                else -> MediaGrid(onOpen)
            }
        }
    }
}

private val monthFmt = DateTimeFormatter.ofPattern("LLLL yyyy", Locale.getDefault())
private fun local(iso: String) = try { Instant.parse(iso).atZone(ZoneId.systemDefault()) } catch (_: Exception) { Instant.now().atZone(ZoneId.systemDefault()) }

@Composable
private fun Timeline(list: List<Preview>?, complete: Boolean, onOpen: (Long, String) -> Unit, onMore: () -> Unit) {
    val ctx = LocalContext.current
    if (list == null) { Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }; return }
    if (list.isEmpty()) {
        Column(Modifier.fillMaxSize().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
            PukkiPose("lesen", 96)
            Text(stringResource(R.string.journal_empty), style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 12.dp))
            Text(stringResource(R.string.journal_empty_hint), color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 6.dp))
        }
        return
    }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(12.dp, 8.dp, 12.dp, 96.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        var last = ""
        list.forEach { e ->
            val month = local(e.date).format(monthFmt).replaceFirstChar { it.uppercase() }
            if (month != last) {
                last = month
                item(key = "m$month") { Text(month, style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 8.dp)) }
            }
            item(key = e.id) { EntryCard(e) { App.get(ctx).journal.order = list.map { it.journal to it.uri }; onOpen(e.journal, e.uri) } }
        }
        if (!complete) item(key = "more") { TextButton(onClick = onMore, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.journal_older)) } }
    }
}

@Composable
private fun EntryCard(e: Preview, onClick: () -> Unit) {
    val d = local(e.date)
    Card(Modifier.fillMaxWidth().clickable(onClick = onClick)) {
        Row(Modifier.padding(12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Column(Modifier.width(44.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Text(d.dayOfMonth.toString(), fontSize = 24.sp, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.primary)
                Text(d.format(DateTimeFormatter.ofPattern("EEE", Locale.getDefault())), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text(d.format(DateTimeFormatter.ofPattern("HH:mm")), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                e.title?.takeIf { it.isNotBlank() }?.let { Text(it, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis) }
                if (e.snippet.isNotBlank()) Text(e.snippet, maxLines = 3, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                val meta = listOfNotNull(
                    e.mood?.let { moodEmoji(it) },
                    e.place?.let { p -> p.name?.takeIf { it.isNotBlank() } ?: if (p.lat != null) "📍" else null }?.let { if (it == "📍") it else "📍 $it" },
                    e.weather?.let { "🌤 $it" },
                ) + e.tags.map { "#$it" } + listOfNotNull(e.owner?.let { "👥 $it" })
                if (meta.isNotEmpty() || e.private) Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(meta.joinToString("  "), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                    if (e.private) Icon(Icons.Default.Lock, contentDescription = stringResource(R.string.journal_private_short), Modifier.size(14.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            val pics = e.media.filter { it.isImage || it.isVideo }
            if (pics.isNotEmpty()) Box(Modifier.size(80.dp).clip(RoundedCornerShape(8.dp))) {
                Thumb(e.journal, pics[0], Modifier.fillMaxSize())
                if (pics.size > 1) Text("+${pics.size - 1}", Modifier.align(Alignment.BottomEnd).padding(4.dp).clip(RoundedCornerShape(8.dp))
                    .background(Color.Black.copy(alpha = 0.55f)).padding(horizontal = 6.dp), color = Color.White, fontSize = 12.sp)
            }
        }
    }
}

/** Vorschaubild einer Datei des Tagebuchs (auch aus einem geteilten). */
@Composable
fun Thumb(journal: Long, m: Media, modifier: Modifier = Modifier, size: Int = 256, scale: ContentScale = ContentScale.Crop) {
    val app = App.get(LocalContext.current)
    val bmp by produceState<android.graphics.Bitmap?>(null, journal, m.path) { value = app.journal.thumb(journal, m.path, size) }
    Box(modifier.background(MaterialTheme.colorScheme.surfaceVariant)) {
        bmp?.let { Image(it.asImageBitmap(), contentDescription = null, Modifier.fillMaxSize(), contentScale = scale) }
        if (m.isVideo) Text("▶", Modifier.align(Alignment.BottomStart).padding(4.dp), color = Color.White)
    }
}

/**
 * Monate als Raster wie im Web, untereinander zum Durchscrollen: oben der aktuelle, darunter die früheren. Tage mit Eintrag zeigen
 * das erste Foto und die Stimmung; Tippen öffnet den Eintrag (mehrere: der erste, Wischen blättert durch den Tag). Geladen wird
 * ein Monat erst, wenn er ins Bild kommt; Geladenes bleibt beim Zurückscrollen erhalten.
 */
@Composable
private fun MonthCalendar(onOpen: (Long, String) -> Unit, onNewDay: (java.time.LocalDate) -> Unit) {
    val now = remember { java.time.YearMonth.now() }
    val loaded = remember { androidx.compose.runtime.mutableStateMapOf<java.time.YearMonth, List<Preview>>() }
    val names = remember { (0..6).map { java.time.DayOfWeek.of(it + 1).getDisplayName(java.time.format.TextStyle.SHORT, Locale.getDefault()) } }
    Column(Modifier.fillMaxSize().padding(horizontal = 8.dp)) {
        // Wochentage bleiben oben stehen
        Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(3.dp)) {
            names.forEach { Text(it, Modifier.weight(1f), style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = androidx.compose.ui.text.style.TextAlign.Center) }
        }
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 96.dp)) {
            // 30 Jahre zurück - mehr hat kein Tagebuch
            items(360, key = { it }) { back -> MonthGrid(now.minusMonths(back.toLong()), loaded, onOpen, onNewDay) }
        }
    }
}

@Composable
private fun MonthGrid(ym: java.time.YearMonth, loaded: MutableMap<java.time.YearMonth, List<Preview>>, onOpen: (Long, String) -> Unit, onNewDay: (java.time.LocalDate) -> Unit) {
    val app = App.get(LocalContext.current)
    LaunchedEffect(ym) {
        if (ym in loaded) return@LaunchedEffect
        val zone = ZoneId.systemDefault()
        val from = ym.atDay(1).atStartOfDay(zone).toInstant().toString()
        val to = ym.plusMonths(1).atDay(1).atStartOfDay(zone).toInstant().toString()
        loaded[ym] = try { app.journal.entries(from = from, to = to, limit = 1000) } catch (_: Exception) { emptyList() }
    }
    val byDay = loaded[ym].orEmpty().groupBy { local(it.date).dayOfMonth }
    val today = java.time.LocalDate.now()
    val first = ym.atDay(1).dayOfWeek.value - 1   // Montag zuerst
    val rows = (first + ym.lengthOfMonth() + 6) / 7
    Column(Modifier.fillMaxWidth().padding(top = 10.dp, bottom = 4.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
        Text(ym.atDay(1).format(monthFmt).replaceFirstChar { it.uppercase() }, Modifier.padding(start = 4.dp, bottom = 4.dp),
            style = MaterialTheme.typography.titleMedium)
        for (r in 0 until rows) Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(3.dp)) {
            for (c in 0..6) {
                val day = r * 7 + c - first + 1
                if (day !in 1..ym.lengthOfMonth()) { Box(Modifier.weight(1f).aspectRatio(1f)); continue }
                val here = byDay[day].orEmpty()
                val withPic = here.firstOrNull { e -> e.media.any { it.isImage || it.isVideo } }
                val pic = withPic?.media?.firstOrNull { it.isImage || it.isVideo }
                val isToday = ym.atDay(day) == today
                Box(Modifier.weight(1f).aspectRatio(1f).clip(RoundedCornerShape(6.dp))
                    .background(if (here.isNotEmpty()) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.4f))
                    // Tag ohne Eintrag: als Kasten umrahmt, Tippen legt dort einen Eintrag an
                    .then(if (here.isEmpty()) Modifier.border(1.dp, MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(6.dp)) else Modifier)
                    .clickable {
                        if (here.isEmpty()) onNewDay(ym.atDay(day))
                        else { app.journal.order = here.map { it.journal to it.uri }; onOpen(here[0].journal, here[0].uri) }
                    }) {
                    if (pic != null && withPic != null) Thumb(withPic.journal, pic, Modifier.fillMaxSize())
                    Text(day.toString() + (here.firstNotNullOfOrNull { it.mood }?.let { " " + moodEmoji(it) } ?: ""),
                        Modifier.align(Alignment.TopStart).padding(2.dp).clip(RoundedCornerShape(4.dp))
                            .background(if (pic != null) Color.Black.copy(alpha = 0.5f) else Color.Transparent).padding(horizontal = 3.dp),
                        color = if (pic != null) Color.White else MaterialTheme.colorScheme.onSurface, fontSize = 11.sp,
                        fontWeight = if (isToday) FontWeight.Bold else FontWeight.Normal)
                    if (here.isEmpty()) Text("+", Modifier.align(Alignment.Center), color = MaterialTheme.colorScheme.primary.copy(alpha = 0.55f), fontSize = 16.sp)
                }
            }
        }
    }
}

@Composable
private fun OnThisDayList(onOpen: (Long, String) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(LocalContext.current)
    val list by produceState<List<OnThisDay>?>(null) { value = try { app.journal.onThisDay() } catch (_: Exception) { emptyList() } }
    val l = list
    if (l == null) { Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }; return }
    if (l.isEmpty()) { Text(stringResource(R.string.journal_onthisday_empty), Modifier.padding(24.dp), color = MaterialTheme.colorScheme.onSurfaceVariant); return }
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(12.dp, 8.dp, 12.dp, 96.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        l.forEach { g ->
            item(key = "h" + g.label) { Text(g.label.replaceFirstChar { it.uppercase() }, style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 8.dp)) }
            items(g.entries, key = { g.label + it.id }) { e -> EntryCard(e) { App.get(ctx).journal.order = emptyList(); onOpen(e.journal, e.uri) } }
        }
    }
}

@Composable
private fun MediaGrid(onOpen: (Long, String) -> Unit) {
    val app = App.get(LocalContext.current)
    val list by produceState<List<MediaHit>?>(null) { value = try { app.journal.media() } catch (_: Exception) { emptyList() } }
    val l = list
    if (l == null) { Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }; return }
    if (l.isEmpty()) { Text(stringResource(R.string.journal_media_empty), Modifier.padding(24.dp), color = MaterialTheme.colorScheme.onSurfaceVariant); return }
    LazyVerticalGrid(GridCells.Adaptive(110.dp), Modifier.fillMaxSize(), contentPadding = PaddingValues(4.dp, 4.dp, 4.dp, 96.dp),
        horizontalArrangement = Arrangement.spacedBy(4.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        items(l, key = { it.media.path }) { h ->
            Thumb(h.journal, h.media, Modifier.aspectRatio(1f).clip(RoundedCornerShape(6.dp)).clickable {
                val (j, uri) = h.entry.split('/', limit = 2).let { it[0].toLong() to it[1] }
                onOpen(j, uri)
            })
        }
    }
}
