package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.OutlinedButton
import androidx.compose.foundation.layout.PaddingValues
import android.content.Intent
import android.graphics.Bitmap
import android.net.Uri
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Star
import androidx.compose.material3.Badge
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.collectAsState
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
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.dashboard.DashWidget
import at.dimes.dmoffice.dashboard.bool
import at.dimes.dmoffice.dashboard.dbl
import at.dimes.dmoffice.dashboard.int
import at.dimes.dmoffice.dashboard.list
import at.dimes.dmoffice.dashboard.long
import at.dimes.dmoffice.dashboard.obj
import at.dimes.dmoffice.dashboard.str
import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.Thumbs
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import java.time.LocalDate
import java.time.format.TextStyle
import java.util.Locale

/** Wo die Diashow je Fotos-Widget stand (solange die App läuft). */
private val photoAt = HashMap<String, Int>()

/** Wohin ein Widget führt: Reiter, Kalendertag, eine Seite der App; dazu die Leiste unten für Meldungen. */
class DashNav(val tab: (Tab) -> Unit, val day: (LocalDate) -> Unit, val go: (String) -> Unit, val snack: SnackbarHostState, val scope: CoroutineScope)

/** Daten eines Widgets: beim Öffnen, jede Minute und nach [reload] neu vom Server; Fehler bleiben beim Widget. */
@Stable
class WidgetData {
    var data by mutableStateOf<JsonObject?>(null)
    var error by mutableStateOf<String?>(null)
    var n by mutableIntStateOf(0)
    fun reload() { n++ }
}

@Composable
fun rememberWidgetData(type: String, opts: JsonObject?, tick: Int, every: Long = 60_000): WidgetData {
    val app = App.get(LocalContext.current)
    val d = remember(type, opts) { WidgetData() }
    val lockChanged by TresorGate.changed.collectAsState()
    LaunchedEffect(type, opts, tick, d.n, lockChanged) {
        while (true) {
            try { d.data = withContext(Dispatchers.IO) { app.dashboard.data(type, opts) }; d.error = null }
            catch (e: CancellationException) { throw e }
            catch (e: at.dimes.dmoffice.api.ApiException) { d.error = e.message ?: e.toString() }
            catch (e: Exception) { d.error = e.message ?: e.toString() }
            delay(every)
        }
    }
    return d
}

/** Name und kurze Beschreibung je Widget-Art; Größe beim Hinzufügen wie im Web. */
data class WidgetInfo(val label: Int, val hint: Int, val size: Int)
val WIDGETS: Map<String, WidgetInfo> = linkedMapOf(
    "today" to WidgetInfo(R.string.dash_w_today, R.string.dash_h_today, 2),
    "events" to WidgetInfo(R.string.dash_w_events, R.string.dash_h_events, 3),
    "meals" to WidgetInfo(R.string.dash_w_meals, R.string.dash_h_meals, 2),
    "tasks" to WidgetInfo(R.string.dash_w_tasks, R.string.dash_h_tasks, 3),
    "shopping" to WidgetInfo(R.string.dash_w_shopping, R.string.dash_h_shopping, 1),
    "mail" to WidgetInfo(R.string.dash_w_mail, R.string.dash_h_mail, 1),
    "chat" to WidgetInfo(R.string.dash_w_chat, R.string.dash_h_chat, 1),
    "pukki" to WidgetInfo(R.string.dash_w_pukki, R.string.dash_h_pukki, 2),
    "weather" to WidgetInfo(R.string.dash_w_weather, R.string.dash_h_weather, 2),
    "news" to WidgetInfo(R.string.dash_w_news, R.string.dash_h_news, 2),
    "journal" to WidgetInfo(R.string.dash_w_journal, R.string.dash_h_journal, 2),
    "photos" to WidgetInfo(R.string.dash_w_photos, R.string.dash_h_photos, 2),
    "notes" to WidgetInfo(R.string.dash_w_notes, R.string.dash_h_notes, 2),
    "files" to WidgetInfo(R.string.dash_w_files, R.string.dash_h_files, 2),
    "birthdays" to WidgetInfo(R.string.dash_w_birthdays, R.string.dash_h_birthdays, 1),
    "countdown" to WidgetInfo(R.string.dash_w_countdown, R.string.dash_h_countdown, 1),
    "bookmarks" to WidgetInfo(R.string.dash_w_bookmarks, R.string.dash_h_bookmarks, 1),
    "storage" to WidgetInfo(R.string.dash_w_storage, R.string.dash_h_storage, 1),
    "coach" to WidgetInfo(R.string.dash_w_coach, R.string.dash_h_coach, 2),
    "water" to WidgetInfo(R.string.dash_w_water, R.string.dash_h_water, 1),
    "system" to WidgetInfo(R.string.dash_w_system, R.string.dash_h_system, 2),
    "stack" to WidgetInfo(R.string.dash_w_stack, R.string.dash_h_stack, 2),
)

@Composable
fun widgetLabel(w: DashWidget): String = w.title?.takeIf { it.isNotBlank() } ?: WIDGETS[w.type]?.let { stringResource(it.label) } ?: w.type

/** Inhalt eines Widgets; big: groß (2×2) - mehr Zeilen. */
@Composable
fun WidgetBody(w: DashWidget, big: Boolean, tick: Int, nav: DashNav) {
    when (w.type) {
        "today" -> TodayWidget(w, tick)
        "events" -> EventsWidget(w, big, tick, nav)
        "meals" -> MealsWidget(w, big, tick, nav)
        "tasks" -> TasksWidget(w, big, tick, nav)
        "shopping" -> ShoppingWidget(w, tick, nav)
        "mail" -> MailWidget(w, big, tick, nav)
        "chat" -> ChatWidget(w, big, tick, nav)
        "pukki" -> PukkiWidget(w, tick, nav)
        "weather" -> WeatherWidget(w, big, tick)
        "news" -> NewsWidget(w, big, tick)
        "journal" -> JournalWidget(w, big, tick, nav)
        "photos" -> PhotosWidget(w, big, tick, nav)
        "notes" -> NotesWidget(w, big, tick, nav)
        "files" -> FilesWidget(w, big, tick, nav)
        "birthdays" -> BirthdaysWidget(w, tick, nav)
        "countdown" -> CountdownWidget(w, tick, nav)
        "bookmarks" -> Column { Head(widgetLabel(w)); Muted(stringResource(R.string.dash_bookmarks_web)) }
        "storage" -> StorageWidget(w, tick)
        "coach" -> CoachWidget(w, tick, nav)
        "water" -> WaterWidget(w, tick, nav)
        "system" -> SystemWidget(w, tick)
        else -> Muted(w.type)
    }
}

// ---------------- Bausteine ----------------

@Composable
private fun Head(label: String, onClick: (() -> Unit)? = null) {
    Text(label, style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.primary, maxLines = 1,
        overflow = TextOverflow.Ellipsis, modifier = Modifier.fillMaxWidth().padding(bottom = 6.dp).let { if (onClick != null) it.clickable(onClick = onClick) else it })
}

@Composable
private fun Muted(text: String, modifier: Modifier = Modifier) =
    Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = modifier)

/** Laden oder Fehler; sonst der Inhalt. */
@Composable
private fun Loaded(d: WidgetData, label: String? = null, content: @Composable (JsonObject) -> Unit) {
    val data = d.data
    when {
        data != null -> content(data)
        d.error != null -> Column { if (label != null) Head(label); Muted(stringResource(R.string.dash_failed, d.error ?: "")) }
        else -> Box(Modifier.fillMaxWidth().padding(8.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) }
    }
}

@Composable
private fun Blank(label: String, text: String, onClick: (() -> Unit)? = null) = Column { Head(label, onClick); Muted(text) }

@Composable
private fun ColumnScope.Scroll(content: @Composable ColumnScope.() -> Unit) =
    Column(Modifier.weight(1f, fill = true).fillMaxWidth().verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(4.dp), content = content)

@Composable
private fun QuickInput(hint: String, onSend: suspend (String) -> Unit) {
    var text by remember { mutableStateOf("") }
    val scope = rememberCoroutineScope()
    fun send() { val t = text.trim(); if (t.isEmpty()) return; scope.launch { onSend(t); text = "" } }
    OutlinedTextField(text, { text = it }, Modifier.fillMaxWidth().height(48.dp), placeholder = { Text(hint, style = MaterialTheme.typography.bodySmall, maxLines = 1) },
        singleLine = true, textStyle = MaterialTheme.typography.bodySmall, keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
        keyboardActions = KeyboardActions(onSend = { send() }),
        trailingIcon = { IconButton(onClick = { send() }) { Icon(Icons.AutoMirrored.Filled.Send, contentDescription = stringResource(R.string.dash_send), modifier = Modifier.size(18.dp)) } })
}

/** Aktion im Hintergrund; ein Fehler kommt als Meldung unten. */
private fun DashNav.run(block: suspend () -> Unit) = scope.launch {
    try { withContext(Dispatchers.IO) { block() } } catch (e: CancellationException) { throw e } catch (e: Exception) { snack.showSnackbar(e.message ?: e.toString()) }
}

private fun dashColor(s: String?): Color? = try { s?.let { Color(android.graphics.Color.parseColor(it)) } } catch (_: Exception) { null }
private fun weekdayShort(iso: String): String = try { LocalDate.parse(iso).dayOfWeek.getDisplayName(TextStyle.SHORT, Locale.getDefault()) } catch (_: Exception) { iso }

// ---------------- Heute ----------------

@Composable
private fun TodayWidget(w: DashWidget, tick: Int) {
    val d = rememberWidgetData("today", w.opts, tick, 300_000)
    Loaded(d) { data ->
        Row(Modifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(data.str("greeting") ?: "", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Muted(data.str("date") ?: "")
            }
            val wx = data.obj("weather")
            val now = wx?.obj("now")
            if (wx != null) Column(horizontalAlignment = Alignment.End) {
                Text("${(now?.dbl("temp") ?: wx.obj("today")?.dbl("max") ?: 0.0).let { Math.round(it) }}°", fontSize = 36.sp, fontWeight = FontWeight.Light)
                Muted(listOfNotNull(wx.obj("today")?.str("text"), wx.str("place")).joinToString(" · "))
            }
        }
    }
}

// ---------------- Kalender ----------------

@Composable
private fun EventsWidget(w: DashWidget, big: Boolean, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("events", w.opts, tick)
    val app = App.get(LocalContext.current)
    Loaded(d, widgetLabel(w)) { data ->
        val days = data.list("days")
        val invites = data.list("invites")
        Column(Modifier.fillMaxSize()) {
            Scroll {
                for (inv in invites) Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).background(MaterialTheme.colorScheme.primaryContainer).padding(8.dp)) {
                    Text(inv.str("text") ?: "", style = MaterialTheme.typography.bodySmall, maxLines = 3, overflow = TextOverflow.Ellipsis)
                    Row {
                        for (a in inv.list("actions")) TextButton(onClick = {
                            nav.run { app.dashboard.post("/api/chat/messages/${inv.long("message")}/actions/${a.str("key")}", JsonObject(emptyMap())); d.reload() }
                        }) { Text(a.str("label") ?: "") }
                    }
                }
                days.forEachIndexed { i, day ->
                    val items = day.list("items")
                    if (i > 0 && items.isEmpty()) return@forEachIndexed
                    Text(if (i == 0) widgetLabel(w) + " · " + (day.str("weekday") ?: "") else "${day.str("label")} · ${day.str("weekday")}".uppercase(),
                        style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = if (i > 0) 4.dp else 0.dp))
                    if (items.isEmpty()) Muted(stringResource(R.string.dash_no_events))
                    for (e in items.take(if (big) 8 else 4)) EventLine(e) { nav.day(LocalDate.parse(day.str("day"))) }
                }
            }
        }
    }
}

@Composable
private fun EventLine(e: JsonObject, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.width(3.dp).height(30.dp).clip(RoundedCornerShape(2.dp)).background(MaterialTheme.colorScheme.primary))
        Spacer(Modifier.width(8.dp))
        Column(Modifier.weight(1f)) {
            Text(listOfNotNull(e.str("icon"), e.str("title")).joinToString(" "), style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            val time = if (e.bool("allDay")) stringResource(R.string.dash_all_day) else "${e.str("start")}–${e.str("end")}"
            val soon = when {
                e.bool("now") -> stringResource(R.string.dash_now)
                e.int("inMinutes") != null -> e.int("inMinutes")!!.let { m -> if (m < 60) stringResource(R.string.dash_in_min, m) else stringResource(R.string.dash_in_h, m / 60) }
                else -> null
            }
            Row {
                Muted(time)
                if (soon != null) Text(" · $soon", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
            }
        }
    }
}

// ---------------- Essensplan ----------------

@Composable
private fun MealsWidget(w: DashWidget, big: Boolean, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("meals", w.opts, tick, 300_000)
    Loaded(d, widgetLabel(w)) { data ->
        if (data.list("plans").isEmpty()) { Blank(widgetLabel(w), stringResource(R.string.dash_no_meal_plan)) { nav.tab(Tab.Calendar) }; return@Loaded }
        Column(Modifier.fillMaxSize()) {
            Head(widgetLabel(w)) { nav.tab(Tab.Calendar) }
            Scroll {
                for (day in data.list("days").take(if (big) 7 else 3)) Row(Modifier.fillMaxWidth().clickable { nav.tab(Tab.Calendar) }) {
                    Text(day.str("weekday") ?: "", style = MaterialTheme.typography.labelMedium, modifier = Modifier.width(40.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
                    val items = day.list("items")
                    Column(Modifier.weight(1f)) {
                        if (items.isEmpty()) Muted(stringResource(R.string.dash_nothing_planned))
                        for (m in items) Text(listOfNotNull(m.str("meal"), m.str("title")).joinToString(": "), style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
            }
        }
    }
}

// ---------------- Aufgaben ----------------

@Composable
private fun TasksWidget(w: DashWidget, big: Boolean, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("tasks", w.opts, tick)
    val ctx = LocalContext.current
    val app = App.get(ctx)
    Loaded(d, widgetLabel(w)) { data ->
        val items = data.list("items")
        val follow = data.list("followUps")
        val today = LocalDate.now().toString()
        val count = items.count { (it.str("due") ?: "") <= today } + follow.count { (it.str("due") ?: "") <= today }
        val doneText = stringResource(R.string.dash_task_done, "%s")
        val undoText = stringResource(R.string.dash_undo)
        Column(Modifier.fillMaxSize()) {
            Row(Modifier.fillMaxWidth().clickable { nav.tab(Tab.Tasks) }, verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(28.dp).clip(CircleShape).background(MaterialTheme.colorScheme.primary), contentAlignment = Alignment.Center) {
                    Icon(Icons.Default.Check, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimary, modifier = Modifier.size(16.dp))
                }
                Spacer(Modifier.width(8.dp))
                Text(widgetLabel(w), style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary, modifier = Modifier.weight(1f))
                Text("$count", fontSize = 26.sp, fontWeight = FontWeight.SemiBold)
            }
            Scroll {
                if (items.isEmpty() && follow.isEmpty()) Muted(stringResource(R.string.dash_nothing_due))
                for (t in items.take(if (big) 12 else 5)) Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(false, onCheckedChange = {
                        val list = t.long("list") ?: return@Checkbox; val uri = t.str("uri") ?: return@Checkbox
                        nav.scope.launch {
                            try { withContext(Dispatchers.IO) { app.dashboard.taskDone(list, uri, true) }; d.reload() } catch (e: Exception) { nav.snack.showSnackbar(e.message ?: e.toString()); return@launch }
                            if (nav.snack.showSnackbar(doneText.format(t.str("title")), undoText, duration = SnackbarDuration.Short) == SnackbarResult.ActionPerformed) {
                                try { withContext(Dispatchers.IO) { app.dashboard.taskDone(list, uri, false) }; d.reload() } catch (_: Exception) { }
                            }
                        }
                    }, modifier = Modifier.size(32.dp))
                    Text(t.str("title") ?: "", style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f).clickable { nav.tab(Tab.Tasks) })
                    val overdue = t.bool("overdue")
                    Text(if (overdue) stringResource(R.string.dash_overdue) else t.str("time") ?: (if (t.str("due") == today) "" else t.str("when") ?: ""),
                        style = MaterialTheme.typography.labelSmall, color = if (overdue) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
                }
                for (m in follow.take(if (big) 6 else 2)) Row(Modifier.fillMaxWidth().clickable {
                    nav.go("message/" + at.dimes.dmoffice.MainActivity.enc(m.str("account") ?: "") + "/" + at.dimes.dmoffice.MainActivity.enc(m.str("folder") ?: "") + "/" + (m.long("uid") ?: 0))
                }.padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("✉ ", style = MaterialTheme.typography.bodySmall)
                    Text(m.str("subject") ?: "", style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                    Text(stringResource(R.string.dash_followup), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            val list = (w.opts?.get("lists") as? JsonArray)?.firstOrNull()?.let { (it as? JsonPrimitive)?.content?.toLongOrNull() } ?: data.long("defaultList")
            if (list != null) QuickInput(stringResource(R.string.dash_new_task)) { raw ->
                val nd = at.dimes.dmoffice.data.NaturalDate.parse(raw)
                val due = nd.date ?: LocalDate.now()
                val body = buildMap<String, kotlinx.serialization.json.JsonElement> {
                    put("list", JsonPrimitive(list)); put("title", JsonPrimitive(nd.title.ifBlank { raw }))
                    if (nd.time != null) { put("due", JsonPrimitive(due.atTime(nd.time).atZone(java.time.ZoneId.systemDefault()).toInstant().toString())); put("dueAllDay", JsonPrimitive(false)) }
                    else { put("due", JsonPrimitive(due.toString())); put("dueAllDay", JsonPrimitive(true)) }
                    put("priority", JsonPrimitive(0)); put("completed", JsonPrimitive(false))
                }
                try { withContext(Dispatchers.IO) { app.dashboard.post("/api/pim/tasks", JsonObject(body)) }; d.reload() } catch (e: Exception) { nav.snack.showSnackbar(e.message ?: e.toString()) }
            }
        }
    }
}

// ---------------- Einkaufsliste ----------------

@Composable
private fun ShoppingWidget(w: DashWidget, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("shopping", w.opts, tick)
    val app = App.get(LocalContext.current)
    Loaded(d, widgetLabel(w)) { data ->
        val list = data.long("list")
        if (list == null) { Blank(widgetLabel(w), stringResource(R.string.dash_no_shopping)) { nav.tab(Tab.Tasks) }; return@Loaded }
        Column(Modifier.fillMaxSize()) {
            Head(w.title?.takeIf { it.isNotBlank() } ?: data.str("name") ?: widgetLabel(w)) { nav.tab(Tab.Tasks) }
            Scroll {
                val items = data.list("items")
                if (items.isEmpty()) Muted(stringResource(R.string.dash_all_bought))
                for (i in items) Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(false, onCheckedChange = { nav.run { app.dashboard.taskDone(list, i.str("uri") ?: "", true); d.reload() } }, modifier = Modifier.size(32.dp))
                    Text(i.str("text") ?: "", style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
            QuickInput(stringResource(R.string.dash_add_item)) { raw ->
                try {
                    withContext(Dispatchers.IO) { app.dashboard.post("/api/pim/tasks", JsonObject(mapOf("list" to JsonPrimitive(list), "title" to JsonPrimitive(raw), "priority" to JsonPrimitive(0), "completed" to JsonPrimitive(false)))) }
                    d.reload()
                } catch (e: Exception) { nav.snack.showSnackbar(e.message ?: e.toString()) }
            }
        }
    }
}

// ---------------- E-Mail ----------------

@Composable
private fun MailWidget(w: DashWidget, big: Boolean, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("mail", w.opts, tick)
    val app = App.get(LocalContext.current)
    Loaded(d, widgetLabel(w)) { data ->
        if (data.list("accounts").isEmpty()) { Blank(widgetLabel(w), stringResource(R.string.dash_no_mail_account)); return@Loaded }
        val mode = w.opts?.str("mode") ?: "all"
        Column(Modifier.fillMaxSize()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.weight(1f)) { Head(w.title?.takeIf { it.isNotBlank() } ?: (widgetLabel(w) + if (mode == "focused") " · " + stringResource(R.string.dash_relevant) else "")) { nav.tab(Tab.Mail) } }
                val n = data.int("unread") ?: 0
                if (n > 0) Badge { Text("$n") }
            }
            Scroll {
                val items = data.list("items")
                if (items.isEmpty()) Muted(stringResource(R.string.dash_nothing_unread))
                for (m in items.take(if (big) 10 else 4)) Row(Modifier.fillMaxWidth().clickable {
                    nav.go("message/" + at.dimes.dmoffice.MainActivity.enc(m.str("account") ?: "") + "/" + at.dimes.dmoffice.MainActivity.enc(m.str("folder") ?: "") + "/" + (m.long("uid") ?: 0))
                }, verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(m.str("from") ?: "", style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(m.str("subject") ?: "", style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    IconButton(onClick = {
                        nav.run {
                            app.dashboard.post("/api/mail/" + Uri.encode(m.str("account") ?: "") + "/flags",
                                JsonObject(mapOf("folder" to JsonPrimitive(m.str("folder") ?: ""), "uids" to JsonArray(listOf(JsonPrimitive(m.long("uid") ?: 0))), "seen" to JsonPrimitive(true))))
                            d.reload()
                        }
                    }, modifier = Modifier.size(32.dp)) { Icon(Icons.Default.Check, contentDescription = stringResource(R.string.dash_mark_read), modifier = Modifier.size(16.dp)) }
                }
            }
        }
    }
}

// ---------------- Chat ----------------

@Composable
private fun ChatWidget(w: DashWidget, big: Boolean, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("chat", w.opts, tick, 30_000)
    Loaded(d, widgetLabel(w)) { data ->
        Column(Modifier.fillMaxSize()) {
            Head(widgetLabel(w)) { nav.tab(Tab.Chat) }
            Scroll {
                val rooms = data.list("rooms")
                if (rooms.isEmpty()) Muted(stringResource(R.string.dash_no_chats))
                for (r in rooms.take(if (big) 8 else 3)) Row(Modifier.fillMaxWidth().clickable { nav.go("chat/${r.long("id")}") }.padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(r.str("title") ?: "", style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.SemiBold, maxLines = 1)
                        r.obj("last")?.let { Text((it.str("from")?.let { f -> "$f: " } ?: "") + (it.str("text") ?: ""), style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                    }
                    val n = r.int("unread") ?: 0
                    if (n > 0) Badge { Text("$n") }
                }
            }
        }
    }
}

// ---------------- Pukki ----------------

@Composable
private fun PukkiWidget(w: DashWidget, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("pukki", w.opts, tick, 300_000)
    val app = App.get(LocalContext.current)
    var answer by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    Loaded(d, "Pukki") { data ->
        val room = data.long("room")
        Column(Modifier.fillMaxSize()) {
            Row(Modifier.fillMaxWidth().clickable(enabled = room != null) { nav.go("chat/$room") }, verticalAlignment = Alignment.CenterVertically) {
                Text("Pukki", style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
                if (data.bool("tomorrow")) Muted(stringResource(R.string.dash_for_tomorrow))
            }
            Scroll { if (busy) Muted(stringResource(R.string.dash_pukki_thinking)) else Text(answer ?: data.str("text") ?: "", style = MaterialTheme.typography.bodySmall) }
            if (room != null) QuickInput(stringResource(R.string.dash_ask_pukki)) { q ->
                busy = true; answer = null
                try { answer = withContext(Dispatchers.IO) { app.dashboard.askPukki(room, q) } } catch (e: Exception) { nav.snack.showSnackbar(e.message ?: e.toString()) } finally { busy = false }
            }
        }
    }
}

// ---------------- Wetter ----------------

fun weatherIcon(code: Int): String = when {
    code == 0 -> "☀️"; code <= 2 -> "🌤️"; code == 3 -> "☁️"; code <= 48 -> "🌫️"; code <= 57 -> "🌦️"; code <= 67 -> "🌧️"
    code <= 77 -> "🌨️"; code <= 82 -> "🌦️"; code <= 86 -> "🌨️"; else -> "⛈️"
}

@Composable
private fun WeatherWidget(w: DashWidget, big: Boolean, tick: Int) {
    val d = rememberWidgetData("weather", w.opts, tick, 600_000)
    Loaded(d, widgetLabel(w)) { data ->
        if (!data.bool("configured")) { Blank(widgetLabel(w), stringResource(R.string.dash_weather_off)); return@Loaded }
        val days = data.list("days")
        if (days.isEmpty()) { Blank(widgetLabel(w), data.str("error") ?: stringResource(R.string.dash_no_weather)); return@Loaded }
        val today = days[0]; val now = data.obj("now")
        Column(Modifier.fillMaxSize()) {
            Head(listOfNotNull(widgetLabel(w), data.str("place")).joinToString(" · "))
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("${Math.round(now?.dbl("temp") ?: today.dbl("max") ?: 0.0)}°", fontSize = 38.sp, fontWeight = FontWeight.Light)
                Spacer(Modifier.width(10.dp))
                Column {
                    Text(weatherIcon(now?.int("code") ?: today.int("code") ?: 0) + " " + (today.str("text") ?: ""), style = MaterialTheme.typography.bodyMedium)
                    Muted(stringResource(R.string.dash_hl, Math.round(today.dbl("max") ?: 0.0).toInt(), Math.round(today.dbl("min") ?: 0.0).toInt()) + (today.int("rain")?.let { " · 🌧 $it %" } ?: ""))
                }
            }
            for (h in data.list("hints").take(if (big) 3 else 1))
                Text("☔ " + stringResource(R.string.dash_rain_from, h.str("start") ?: "", h.str("title") ?: ""), style = MaterialTheme.typography.bodySmall, color = Color(0xFFE0A030), maxLines = 1, overflow = TextOverflow.Ellipsis)
            Spacer(Modifier.weight(1f))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                for (x in days.drop(1).take(if (w.size == 3) 6 else 4)) Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Muted(weatherIcon(x.int("code") ?: 0) + " " + weekdayShort(x.str("date") ?: ""))
                    Text("${Math.round(x.dbl("max") ?: 0.0)}° ${Math.round(x.dbl("min") ?: 0.0)}°", style = MaterialTheme.typography.labelSmall)
                }
            }
        }
    }
}

// ---------------- Schlagzeilen ----------------

@Composable
private fun NewsWidget(w: DashWidget, big: Boolean, tick: Int) {
    val d = rememberWidgetData("news", w.opts, tick, 600_000)
    val ctx = LocalContext.current
    Loaded(d, widgetLabel(w)) { data ->
        if (!data.bool("configured")) { Blank(widgetLabel(w), stringResource(R.string.dash_news_off)); return@Loaded }
        Column(Modifier.fillMaxSize()) {
            Head(widgetLabel(w))
            // Schriftgröße der Überschriften (Einstellung des Widgets), die Quelle bleibt klein und gedämpft
            val size = w.opts?.str("text") ?: "m"
            val style = when (size) {
                "s" -> MaterialTheme.typography.bodySmall
                "l" -> MaterialTheme.typography.titleMedium.copy(fontWeight = FontWeight.SemiBold)
                else -> MaterialTheme.typography.bodyLarge.copy(fontWeight = FontWeight.SemiBold)
            }
            val muted = MaterialTheme.colorScheme.onSurfaceVariant
            Scroll {
                for (h in data.list("items").take(if (big) 12 else 5)) Text(
                    buildAnnotatedString {
                        append(h.str("title") ?: "")
                        withStyle(SpanStyle(fontSize = 11.sp, fontWeight = FontWeight.Normal, color = muted)) { append(" · " + (h.str("source") ?: "")) }
                    },
                    style = style, maxLines = if (size == "s") 2 else 3, overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(vertical = if (size == "s") 0.dp else 3.dp)
                        .clickable { try { ctx.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(h.str("link")))) } catch (_: Exception) { } })
            }
        }
    }
}

// ---------------- Tagebuch ----------------

@Composable
private fun JournalWidget(w: DashWidget, big: Boolean, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("journal", w.opts, tick, 300_000)
    Loaded(d, widgetLabel(w)) { data ->
        val todays = data.list("today")
        val back = data.list("back")
        fun open(e: JsonObject) = nav.go("journal-view/${e.long("journal")}/" + Uri.encode(e.str("uri") ?: ""))
        Column(Modifier.fillMaxSize()) {
            Head(widgetLabel(w) + if (back.isNotEmpty()) " · " + stringResource(R.string.dash_looking_back) else "") { nav.tab(Tab.Journal) }
            Scroll {
                for ((k, g) in back.take(if (big) 3 else 1).withIndex()) {
                    // „vor einer Woche“ als Marke - auf dem Hintergrundbild sonst kaum lesbar
                    androidx.compose.material3.Surface(color = MaterialTheme.colorScheme.primaryContainer, shape = androidx.compose.foundation.shape.RoundedCornerShape(50),
                        modifier = Modifier.padding(top = if (k > 0) 10.dp else 2.dp, bottom = 4.dp)) {
                        Text(g.str("label") ?: "", style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.SemiBold,
                            color = MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.padding(horizontal = 10.dp, vertical = 2.dp))
                    }
                    for (e in g.list("entries").take(if (big) 2 else 1)) Column(Modifier.fillMaxWidth().clickable { open(e) }) {
                        Text(e.str("title") ?: "", style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(e.str("snippet") ?: "", style = MaterialTheme.typography.bodySmall, maxLines = if (big) 4 else 2, overflow = TextOverflow.Ellipsis)
                    }
                }
                if (todays.isNotEmpty()) for (e in todays) Text("✓ " + (e.str("title") ?: e.str("snippet") ?: ""), style = MaterialTheme.typography.bodySmall, maxLines = 1, modifier = Modifier.clickable { open(e) })
                else if (back.isEmpty()) Muted(stringResource(R.string.dash_no_entry_today))
                (data.int("streak") ?: 0).takeIf { it > 1 }?.let { Muted("🔥 " + stringResource(R.string.dash_streak, it)) }
            }
            if (todays.isEmpty()) TextButton(onClick = { nav.go("journal/new") }) { Text(stringResource(R.string.dash_write_today)) }
        }
    }
}

// ---------------- Fotos ----------------

@Composable
private fun PhotosWidget(w: DashWidget, big: Boolean, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("photos", w.opts, tick, 600_000)
    val ctx = LocalContext.current
    Loaded(d, widgetLabel(w)) { data ->
        // Nichts von diesem Tag: die neuesten Fotos, damit das Widget nie leer bleibt
        val onDay = data.list("years").flatMap { y -> y.list("items").map { it to (y.int("yearsAgo") ?: 0) } }
        val all = onDay.ifEmpty { data.list("recent").map { it to -1 } }
        if (all.isEmpty()) { Blank(widgetLabel(w), stringResource(R.string.dash_no_photos)) { nav.tab(Tab.Gallery) }; return@Loaded }
        // Beim nächsten Öffnen mit dem folgenden Foto weiter, nicht wieder mit dem ersten
        var n by remember(all.size) { mutableIntStateOf(photoAt[w.id]?.plus(1) ?: (0 until 1000).random()) }
        LaunchedEffect(n) { photoAt[w.id] = n }
        // Wechseltakt aus den Einstellungen (Sekunden, 0 = gar nicht)
        val every = w.opts?.int("every") ?: 7
        LaunchedEffect(all.size, every) { while (all.size > 1 && every > 0) { delay(every * 1000L); n = (n + 1) % all.size } }
        val (cur, ago) = all[n % all.size]
        val item = FileItem(cur.str("path") ?: "", cur.str("name") ?: "", false, mime = cur.str("mime"), etag = cur.str("etag"))
        val bmp by produceState<Bitmap?>(Thumbs.cached(item, Thumbs.LARGE), item.path) { if (value == null) value = try { Thumbs.load(ctx, item, if (big) Thumbs.LARGE else Thumbs.SMALL) } catch (_: Exception) { null } }
        var viewing by remember { mutableStateOf<Int?>(null) }
        viewing?.let { start ->
            val items = all.map { (o, _) -> FileItem(o.str("path") ?: "", o.str("name") ?: "", false, mime = o.str("mime"), etag = o.str("etag")) }
            // Bildschirmfüllend wie in der Galerie - nicht innerhalb der Kachel
            androidx.compose.ui.window.Dialog(onDismissRequest = { viewing = null },
                properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
                FileViewer(items, start, onClose = { viewing = null }, onShare = null, onOpenExternal = null)
            }
        }
        Box(Modifier.padding(0.dp).fillMaxSize().clip(RoundedCornerShape(12.dp)).clickable { viewing = n % all.size }) {
            bmp?.let { Image(it.asImageBitmap(), contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize()) }
            Column(Modifier.align(Alignment.BottomStart).fillMaxWidth().background(Brush.verticalGradient(listOf(Color.Transparent, Color(0xA6000000)))).padding(10.dp)) {
                Text(if (ago < 0) stringResource(R.string.dash_recent_photos) else stringResource(R.string.dash_on_this_day) + " · " + (if (ago == 1) stringResource(R.string.dash_year_ago) else stringResource(R.string.dash_years_ago, ago)),
                    color = Color.White, style = MaterialTheme.typography.labelLarge)
                cur.long("taken")?.let { Text(java.text.DateFormat.getDateInstance(java.text.DateFormat.LONG).format(java.util.Date(it)), color = Color.White.copy(alpha = .85f), style = MaterialTheme.typography.labelSmall) }
            }
        }
    }
}

// ---------------- Notizen ----------------

@Composable
private fun NotesWidget(w: DashWidget, big: Boolean, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("notes", w.opts, tick)
    val app = App.get(LocalContext.current)
    val created = stringResource(R.string.dash_note_created)
    Loaded(d, widgetLabel(w)) { data ->
        Column(Modifier.fillMaxSize()) {
            Head(widgetLabel(w)) { nav.tab(Tab.Notes) }
            Scroll {
                val items = data.list("items")
                if (items.isEmpty()) Muted(stringResource(R.string.dash_no_notes))
                for (n in items.take(if (big) 8 else 3)) Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp))
                    .background((dashColor(n.str("color")) ?: MaterialTheme.colorScheme.surfaceVariant).copy(alpha = .25f)).clickable { nav.tab(Tab.Notes) }.padding(6.dp),
                    verticalAlignment = Alignment.CenterVertically) {
                    if (n.bool("important")) Icon(Icons.Default.Star, contentDescription = null, tint = Color(0xFFE8A33D), modifier = Modifier.size(14.dp))
                    Text(n.str("title") ?: "", style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(start = 4.dp))
                }
            }
            QuickInput(stringResource(R.string.dash_quick_note)) { raw ->
                try {
                    withContext(Dispatchers.IO) {
                        val a = app.api ?: return@withContext
                        val id = (a.json.parseToJsonElement(a.postText(a.url("/api/notes"), "{}")) as JsonObject).long("id") ?: return@withContext
                        val html = "<p>" + raw.replace("&", "&amp;").replace("<", "&lt;") + "</p>"
                        a.putText(a.url("/api/notes/$id/content"), JsonObject(mapOf("blocks" to JsonArray(listOf(JsonObject(mapOf("html" to JsonPrimitive(html))))), "plain" to JsonPrimitive(raw))).toString())
                    }
                    nav.snack.showSnackbar(created); d.reload()
                } catch (e: Exception) { nav.snack.showSnackbar(e.message ?: e.toString()) }
            }
        }
    }
}

// ---------------- Dateien ----------------

@Composable
private fun FilesWidget(w: DashWidget, big: Boolean, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("files", w.opts, tick)
    Loaded(d, widgetLabel(w)) { data ->
        Column(Modifier.fillMaxSize()) {
            Head(widgetLabel(w)) { nav.tab(Tab.Files) }
            Scroll {
                val items = data.list("items")
                if (items.isEmpty()) Muted(stringResource(R.string.dash_no_files))
                for (f in items.take(if (big) 10 else 4)) {
                    val item = FileItem(f.str("path") ?: "", f.str("name") ?: "", f.bool("isDir"), f.long("size") ?: 0, f.str("mime"), f.str("modified"), f.str("etag"))
                    Row(Modifier.fillMaxWidth().clickable { nav.tab(Tab.Files) }, verticalAlignment = Alignment.CenterVertically) {
                        ThumbOrIcon(item, Modifier.size(24.dp).clip(RoundedCornerShape(4.dp)), 18.dp)
                        Spacer(Modifier.width(8.dp))
                        if (f.bool("favorite")) Icon(Icons.Default.Star, contentDescription = null, tint = Color(0xFFE8A33D), modifier = Modifier.size(12.dp))
                        Text(item.name, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                        if (!item.isDir) Muted(android.text.format.Formatter.formatShortFileSize(LocalContext.current, item.size))
                    }
                }
            }
        }
    }
}

// ---------------- Geburtstage ----------------

@Composable
private fun BirthdaysWidget(w: DashWidget, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("birthdays", w.opts, tick, 600_000)
    val app = App.get(LocalContext.current)
    val days = w.opts?.int("days") ?: 14
    val giftFor = stringResource(R.string.dash_gift_for, "%s")
    val taskCreated = stringResource(R.string.dash_task_created)
    Loaded(d, widgetLabel(w)) { data ->
        Column(Modifier.fillMaxSize()) {
            Head(widgetLabel(w)) { nav.tab(Tab.Contacts) }
            Scroll {
                val items = data.list("items")
                if (items.isEmpty()) Muted(stringResource(R.string.dash_no_birthdays, days))
                for (b in items.take(4)) Column {
                    Text("🎂 " + (b.str("name") ?: ""), style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    val inDays = b.int("inDays") ?: 0
                    Muted(listOfNotNull(
                        when (inDays) { 0 -> stringResource(R.string.dash_today); 1 -> stringResource(R.string.dash_tomorrow); else -> stringResource(R.string.dash_in_days, inDays) },
                        b.int("age")?.let { stringResource(R.string.dash_turns, it) }).joinToString(" · "))
                    Row {
                        b.str("chatWith")?.let { other ->
                            TextButton(onClick = {
                                nav.run {
                                    val a = app.api ?: return@run
                                    val room = (a.json.parseToJsonElement(a.postText(a.url("/api/chat/rooms"), JsonObject(mapOf("members" to JsonArray(listOf(JsonPrimitive(other))))).toString())) as JsonObject).long("id")
                                    if (room != null) withContext(Dispatchers.Main) { nav.go("chat/$room") }
                                }
                            }) { Text(stringResource(R.string.dash_congratulate)) }
                        }
                        TextButton(onClick = {
                            nav.run {
                                val day = b.str("day") ?: LocalDate.now().toString()
                                app.dashboard.post("/api/pim/tasks", JsonObject(mapOf("list" to JsonPrimitive(app.dashboard.data("tasks", null).long("defaultList") ?: return@run),
                                    "title" to JsonPrimitive(giftFor.format(b.str("name"))), "due" to JsonPrimitive(LocalDate.parse(day).minusDays(2).coerceAtLeast(LocalDate.now()).toString()),
                                    "dueAllDay" to JsonPrimitive(true), "priority" to JsonPrimitive(0), "completed" to JsonPrimitive(false))))
                                nav.snack.showSnackbar(taskCreated)
                            }
                        }) { Text(stringResource(R.string.dash_gift)) }
                    }
                }
            }
        }
    }
}

private fun LocalDate.coerceAtLeast(min: LocalDate) = if (isBefore(min)) min else this

// ---------------- Countdown ----------------

@Composable
private fun CountdownWidget(w: DashWidget, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("countdown", w.opts, tick, 600_000)
    Loaded(d, widgetLabel(w)) { data ->
        if (!data.bool("found")) { Blank(widgetLabel(w), stringResource(R.string.dash_no_match)); return@Loaded }
        val n = data.int("days") ?: 0
        Column(Modifier.fillMaxSize().clickable { data.str("day")?.let { nav.day(LocalDate.parse(it)) } }, horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
            Text(if (n <= 0) "🎉" else "$n", fontSize = 40.sp, fontWeight = FontWeight.SemiBold)
            Text(when (n) { 1 -> stringResource(R.string.dash_tomorrow_excl); else -> stringResource(R.string.dash_days_left, n) }, style = MaterialTheme.typography.labelLarge)
            Muted(data.str("title") ?: "", Modifier.padding(top = 2.dp))
        }
    }
}

// ---------------- Speicher ----------------

@Composable
private fun StorageWidget(w: DashWidget, tick: Int) {
    val d = rememberWidgetData("storage", w.opts, tick, 600_000)
    val ctx = LocalContext.current
    fun size(b: Long?) = android.text.format.Formatter.formatShortFileSize(ctx, b ?: 0)
    Loaded(d, widgetLabel(w)) { data ->
        Column(Modifier.fillMaxSize()) {
            Head(widgetLabel(w))
            Text(size(data.long("used")), fontSize = 24.sp, fontWeight = FontWeight.SemiBold)
            Muted(data.long("quota")?.takeIf { it > 0 }?.let { stringResource(R.string.dash_of_quota, size(it)) } ?: stringResource(R.string.dash_no_limit))
            Spacer(Modifier.height(4.dp))
            Muted("${stringResource(R.string.dash_w_files)} ${size(data.long("files"))} · ${stringResource(R.string.dash_database)} ${size(data.long("database"))} · ${stringResource(R.string.dash_trash)} ${size(data.long("trash"))}")
        }
    }
}

// ---------------- Coach ----------------

private val coachJson = kotlinx.serialization.json.Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }
private fun coachDay(o: JsonObject): at.dimes.dmoffice.coach.Day? =
    try { coachJson.decodeFromJsonElement(at.dimes.dmoffice.coach.Day.serializer(), o) } catch (_: Exception) { null }

/**
 * Heute im Coach - wie die beiden Widgets auf dem Startbildschirm: Ring (gegessen, Bewegung, netto) mit dem, was noch geht.
 * Halbe Breite: Ring und drei Balken (gegessen, Schritte, Wasser). Volle Breite: dazu „+“ und Barcode. Groß: wie „Coach · Heute“
 * mit Eiweiß/Fett/Kohlenhydraten, Kacheln Schritte/Wasser/Gewicht, Körperwerten und Knöpfen Essen, Foto, Barcode, + Wasser.
 */
@Composable
private fun CoachWidget(w: DashWidget, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("coach", w.opts, tick, 300_000)
    val ctx = LocalContext.current
    val app = App.get(ctx)
    Loaded(d, widgetLabel(w)) { o ->
        val day = coachDay(o) ?: return@Loaded Muted(w.type)
        val budget = day.budget?.takeIf { it > 0 }
        val eaten = day.eaten.kcal
        val base = budget?.let { it - day.movement }?.takeIf { it > 0 }
        val move = minOf(day.movement, eaten)
        val big = w.size >= 3
        val ringDp = if (big) 96 else 72
        val ring = remember(eaten, move, base, ringDp) {
            if (base == null) at.dimes.dmoffice.widget.CoachWidget.ring(ctx, 0f, 0f, 0f, ringDp)
            else at.dimes.dmoffice.widget.CoachWidget.ring(ctx, (eaten / base).toFloat(), (move / base).toFloat(), ((eaten - move) / base).toFloat(), ringDp)
        }
        fun open(action: String) = try { ctx.startActivity(at.dimes.dmoffice.MainActivity.coachIntent(ctx, action)) } catch (_: Exception) { }
        @Composable fun Ring() = Box(Modifier.size(ringDp.dp), contentAlignment = Alignment.Center) {
            Image(ring.asImageBitmap(), contentDescription = null, modifier = Modifier.size(ringDp.dp))
            CoachRingValue(day, budget, if (big) 18.sp else 15.sp)
        }
        Column(Modifier.fillMaxSize()) {
            if (big) {
                Head(widgetLabel(w)) { nav.tab(Tab.Coach) }
                Row(Modifier.fillMaxWidth().clickable { nav.tab(Tab.Coach) }, verticalAlignment = Alignment.CenterVertically) {
                    Ring(); Spacer(Modifier.width(12.dp)); Box(Modifier.weight(1f)) { CoachTotals(day) }
                }
                Spacer(Modifier.weight(1f))
                CoachTiles(day)
                CoachBodyLine(day)
                Spacer(Modifier.weight(1f))
                val glass = day.water.sizes.firstOrNull() ?: 250
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    CoachAction(R.drawable.ic_restaurant, stringResource(R.string.widget_coach_food), true, Modifier.weight(1f)) { open("add") }
                    CoachAction(R.drawable.ic_journal_photo, stringResource(R.string.widget_coach_photo), false, Modifier.weight(1f)) { open("photo") }
                    CoachAction(R.drawable.ic_barcode, stringResource(R.string.coach_barcode), false, Modifier.weight(1f)) { open("scan") }
                    CoachAction(R.drawable.ic_water_drop, "+$glass", false, Modifier.weight(1f)) {
                        nav.scope.launch { try { app.coach.water(day.date, glass); d.reload() } catch (e: Exception) { nav.snack.showSnackbar(e.message ?: e.toString()) } }
                    }
                }
            } else {
                Head(widgetLabel(w)) { nav.tab(Tab.Coach) }
                Row(Modifier.fillMaxWidth().weight(1f).clickable { nav.tab(Tab.Coach) }, verticalAlignment = Alignment.CenterVertically) {
                    Ring(); Spacer(Modifier.width(10.dp))
                    Box(Modifier.weight(1f)) { CoachLines(day, base) }
                    // Volle Breite: die beiden Knöpfe des kleinen Widgets
                    if (w.size == 2) {
                        Spacer(Modifier.width(10.dp))
                        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            CoachRound(R.drawable.ic_restaurant, stringResource(R.string.coach_add_food), true) { open("add") }
                            CoachRound(R.drawable.ic_barcode, stringResource(R.string.coach_barcode), false) { open("scan") }
                        }
                    }
                }
            }
        }
    }
}

private val CoachOver = Color(0xFFD13438)
private val CoachNet = Color(0xFFE8833A)

private fun coachK(v: Double) = if (kotlin.math.abs(v) < 1000) java.text.NumberFormat.getIntegerInstance().format(Math.round(v))
    else java.text.NumberFormat.getNumberInstance().apply { minimumFractionDigits = 1; maximumFractionDigits = 1 }.format(v / 1000) + "k"
private fun coachWhole(v: Double) = java.text.NumberFormat.getIntegerInstance().format(Math.round(v))

/** Mitte des Rings wie im Widget: was noch geht (rot mit „+“, wenn drüber), ohne Bedarf das Gegessene, bei verborgenen Zahlen der Anteil. */
@Composable
private fun CoachRingValue(d: at.dimes.dmoffice.coach.Day, budget: Double?, size: androidx.compose.ui.unit.TextUnit) {
    val eaten = d.eaten.kcal
    val (value, over) = when {
        budget == null -> coachK(eaten) to false
        d.hideNumbers -> "${Math.round(eaten / budget * 100)} %" to (eaten > budget)
        eaten <= budget -> coachK(budget - eaten) to false
        else -> "+" + coachK(eaten - budget) to true
    }
    Text(value, fontSize = size, fontWeight = FontWeight.Bold, maxLines = 1, color = if (over) CoachOver else MaterialTheme.colorScheme.onSurface)
}

/** Drei Zeilen wie im kleinen Widget: Symbol, Balken, Wert. */
@Composable
private fun CoachLines(d: at.dimes.dmoffice.coach.Day, base: Double?) {
    @Composable fun Line(icon: Int, color: Color, part: Float, barColor: Color, value: String, extra: Color? = null) =
        Row(Modifier.fillMaxWidth().padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(painterResource(icon), contentDescription = null, tint = color, modifier = Modifier.size(14.dp))
            Spacer(Modifier.width(6.dp))
            val over = extra != null && part > 1f
            LinearProgressIndicator(progress = { if (over) 1f / part else part.coerceIn(0f, 1f) }, modifier = Modifier.weight(1f).height(6.dp),
                color = barColor, trackColor = if (over) extra!! else Color(0x40888888), drawStopIndicator = {}, gapSize = 0.dp)
            Spacer(Modifier.width(6.dp))
            Text(value, maxLines = 1, style = MaterialTheme.typography.bodySmall, fontWeight = FontWeight.Medium,
                textAlign = androidx.compose.ui.text.style.TextAlign.End, modifier = Modifier.width(44.dp))
        }
    Column(Modifier.fillMaxWidth()) {
        val eatenPart = if (base != null) (d.eaten.kcal / base).toFloat() else 0f
        Line(R.drawable.ic_restaurant, at.dimes.dmoffice.widget.CoachEatenColor, eatenPart, if (eatenPart > 1f) CoachOver else at.dimes.dmoffice.widget.CoachEatenColor,
            if (d.hideNumbers) "${Math.round(eatenPart * 100)} %" else coachK(d.eaten.kcal))
        (d.activity?.steps ?: d.steps.steps)?.let { steps ->
            val goal = d.steps.goal
            Line(R.drawable.ic_walk, at.dimes.dmoffice.widget.CoachStepsColor, if (goal > 0) steps.toFloat() / goal else 0f,
                if (goal in 1..steps) StepsDoneColor else at.dimes.dmoffice.widget.CoachStepsColor, coachK(steps.toDouble()), Color(0xFF9BE07A))
        }
        val l = java.text.NumberFormat.getNumberInstance().apply { maximumFractionDigits = 1 }
        Line(R.drawable.ic_water_drop, at.dimes.dmoffice.widget.CoachWaterColor, if (d.water.goal > 0) d.water.ml.toFloat() / d.water.goal else 0f,
            at.dimes.dmoffice.widget.CoachWaterColor, l.format(d.water.ml / 1000.0) + " l")
    }
}

/** Gegessen und Bewegung, darunter Eiweiß, Fett, Kohlenhydrate mit Balken zum Tagesziel (wie im großen Widget). */
@Composable
private fun CoachTotals(d: at.dimes.dmoffice.coach.Day) {
    val muted = MaterialTheme.colorScheme.onSurfaceVariant
    Column(Modifier.fillMaxWidth()) {
        if (!d.hideNumbers) {
            Row(Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.coach_scale_eaten), color = muted, style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
                Text("+" + coachWhole(d.eaten.kcal), color = at.dimes.dmoffice.widget.CoachEatenColor, style = MaterialTheme.typography.bodySmall, fontWeight = FontWeight.Medium)
            }
            if (d.movement > 0) Row(Modifier.fillMaxWidth()) {
                Text(stringResource(R.string.coach_head_move), color = muted, style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
                Text("−" + coachWhole(d.movement), color = at.dimes.dmoffice.widget.CoachStepsColor, style = MaterialTheme.typography.bodySmall, fontWeight = FontWeight.Medium)
            }
        }
        @Composable fun Macro(label: Int, v: Double, goal: Double?, color: Color) {
            Row(Modifier.fillMaxWidth().padding(top = 3.dp)) {
                Text(stringResource(label), color = muted, style = MaterialTheme.typography.labelSmall, modifier = Modifier.weight(1f))
                if (!d.hideNumbers) Text(coachWhole(v) + (goal?.let { " / " + coachWhole(it) } ?: "") + " g", color = muted, style = MaterialTheme.typography.labelSmall)
            }
            LinearProgressIndicator(progress = { if (goal != null && goal > 0) (v / goal).toFloat().coerceIn(0f, 1f) else 0f },
                modifier = Modifier.fillMaxWidth().height(4.dp), color = color, trackColor = Color(0x40888888), drawStopIndicator = {}, gapSize = 0.dp)
        }
        Macro(R.string.coach_protein, d.eaten.protein, d.targets.protein, Color(0xFFE0675B))
        Macro(R.string.coach_fat, d.eaten.fat, d.targets.fat, Color(0xFF4FB3A0))
        Macro(R.string.coach_carbs, d.eaten.carbs, d.targets.carbs, Color(0xFFE0B14E))
    }
}

/** Kacheln Schritte, Wasser, Gewicht. */
@Composable
private fun CoachTiles(d: at.dimes.dmoffice.coach.Day) {
    val l = java.text.NumberFormat.getNumberInstance().apply { maximumFractionDigits = 1 }
    @Composable fun T(label: Int, value: String, part: Float?, color: Color, modifier: Modifier) =
        Column(modifier.clip(RoundedCornerShape(12.dp)).background(MaterialTheme.colorScheme.secondaryContainer).padding(horizontal = 8.dp, vertical = 6.dp)) {
            Text(stringResource(label), maxLines = 1, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSecondaryContainer)
            Text(value, maxLines = 1, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, color = MaterialTheme.colorScheme.onSecondaryContainer)
            if (part != null) LinearProgressIndicator(progress = { part.coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth().height(4.dp),
                color = color, trackColor = Color(0x40888888), drawStopIndicator = {}, gapSize = 0.dp)
        }
    Row(Modifier.fillMaxWidth().padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        val steps = d.activity?.steps ?: d.steps.steps
        T(R.string.coach_steps, steps?.let { coachWhole(it.toDouble()) } ?: "–", steps?.let { s -> if (d.steps.goal > 0) s.toFloat() / d.steps.goal else null },
            if (steps != null && d.steps.goal in 1..steps) StepsDoneColor else at.dimes.dmoffice.widget.CoachStepsColor, Modifier.weight(1f))
        T(R.string.coach_water, l.format(d.water.ml / 1000.0) + " / " + l.format(d.water.goal / 1000.0) + " l",
            if (d.water.goal > 0) d.water.ml.toFloat() / d.water.goal else null, at.dimes.dmoffice.widget.CoachWaterColor, Modifier.weight(1f))
        T(R.string.coach_weight, d.weight?.let { l.format(it) + " kg" } ?: "–", null, at.dimes.dmoffice.widget.CoachWaterColor, Modifier.weight(1f))
    }
}

/** Eine Zeile Körperwerte von der Uhr - nur was da ist. */
@Composable
private fun CoachBodyLine(d: at.dimes.dmoffice.coach.Day) {
    val ctx = LocalContext.current
    val parts = listOfNotNull(
        d.activity?.sleepMin?.let { ctx.getString(R.string.coach_sleep) + " " + "%d:%02d".format(it / 60, it % 60) },
        d.activity?.restingHr?.let { ctx.getString(R.string.coach_resting_hr) + " $it" },
        d.extra.hrv?.let { "HRV " + coachWhole(it) },
        if (d.extra.bodyBatteryLow != null || d.extra.bodyBatteryHigh != null) "BB ${d.extra.bodyBatteryLow?.let { coachWhole(it) } ?: "–"}–${d.extra.bodyBatteryHigh?.let { coachWhole(it) } ?: "–"}" else null,
    )
    if (parts.isNotEmpty()) Text(parts.joinToString("  ·  "), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.labelSmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 6.dp))
}

@Composable
private fun CoachAction(icon: Int, caption: String, primary: Boolean, modifier: Modifier, onClick: () -> Unit) {
    val bg = if (primary) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.secondaryContainer
    val fg = if (primary) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSecondaryContainer
    Column(modifier.height(48.dp).clip(RoundedCornerShape(14.dp)).background(bg).clickable(onClick = onClick),
        horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
        Icon(painterResource(icon), contentDescription = null, tint = fg, modifier = Modifier.size(18.dp))
        Text(caption, maxLines = 1, color = fg, style = MaterialTheme.typography.labelSmall)
    }
}

@Composable
private fun CoachRound(icon: Int, description: String, primary: Boolean, onClick: () -> Unit) {
    val bg = if (primary) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.secondaryContainer
    val fg = if (primary) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSecondaryContainer
    Box(Modifier.size(40.dp).clip(CircleShape).background(bg).clickable(onClick = onClick), contentAlignment = Alignment.Center) {
        Icon(painterResource(icon), contentDescription = description, tint = fg, modifier = Modifier.size(22.dp))
    }
}

/** Wasser heute mit Knöpfen für die eigenen Gefäße. */
@Composable
private fun WaterWidget(w: DashWidget, tick: Int, nav: DashNav) {
    val d = rememberWidgetData("water", w.opts, tick, 300_000)
    val app = App.get(LocalContext.current)
    Loaded(d, widgetLabel(w)) { o ->
        val day = coachDay(o) ?: return@Loaded Muted(w.type)
        val water = day.water
        Column(Modifier.fillMaxSize()) {
            Head(widgetLabel(w)) { nav.tab(Tab.Coach) }
            Text("%.2f l".format(water.ml / 1000.0) + "  /  " + "%.1f l".format(water.goal / 1000.0), fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
            LinearProgressIndicator(progress = { if (water.goal > 0) (water.ml.toFloat() / water.goal).coerceIn(0f, 1f) else 0f },
                modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp), color = Color(0xFF1E88E5))
            Spacer(Modifier.weight(1f))
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                for (n in (water.sizes.takeIf { it.isNotEmpty() } ?: listOf(250)).take(2)) OutlinedButton(onClick = {
                    nav.scope.launch { try { app.coach.water(day.date, n); d.reload() } catch (e: Exception) { nav.snack.showSnackbar(e.message ?: e.toString()) } }
                }, contentPadding = PaddingValues(horizontal = 10.dp, vertical = 0.dp)) { Text(if (n >= 1000) "+%.1f l".format(n / 1000.0) else "+$n ml", fontSize = 12.sp) }
            }
        }
    }
}

// ---------------- System (Verwaltung) ----------------

@Composable
private fun SystemWidget(w: DashWidget, tick: Int) {
    val d = rememberWidgetData("system", w.opts, tick, 300_000)
    val ctx = LocalContext.current
    val data = d.data
    if (data == null && d.error?.contains("403") == true || d.error?.contains("Verwaltung") == true) { Blank(widgetLabel(w), stringResource(R.string.dash_admin_only)); return }
    Loaded(d, widgetLabel(w)) { x ->
        val green = Color(0xFF3FB950); val yellow = Color(0xFFE3B341); val red = Color(0xFFF85149)
        @Composable fun line(color: Color, label: String, value: String) = Row(Modifier.fillMaxWidth().padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(8.dp).clip(CircleShape).background(color)); Spacer(Modifier.width(8.dp))
            Text(label, style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f)); Muted(value)
        }
        Column(Modifier.fillMaxSize()) {
            Head(widgetLabel(w) + " · " + stringResource(R.string.dash_version, x.str("version") ?: ""))
            val st = x.obj("storage"); val bk = x.obj("backup"); val vol = x.obj("volume"); val pr = x.obj("profile")
            line(when { st?.bool("on") != true -> yellow; st.str("lastError") != null -> red; else -> green }, stringResource(R.string.dash_external), if (st?.bool("on") == true) (st.str("mode") ?: "") else stringResource(R.string.dash_off))
            line(when { bk?.bool("enabled") != true -> yellow; bk.str("lastError") != null || bk.str("blocked") != null -> red; else -> green }, stringResource(R.string.dash_core_backup),
                if (bk?.bool("enabled") == true) bk.str("lastRun")?.take(10) ?: stringResource(R.string.dash_never) else stringResource(R.string.dash_off))
            line(when { pr?.str("at") == null -> red; pr.bool("current") -> green; else -> yellow }, stringResource(R.string.dash_profile),
                stringResource(when { pr?.str("at") == null -> R.string.dash_never_saved; pr.bool("current") -> R.string.dash_saved; else -> R.string.dash_outdated }))
            if (vol != null) {
                val free = vol.long("free") ?: 0; val total = vol.long("total") ?: 1
                line(if (free * 10 < total) red else if (free * 5 < total) yellow else green, stringResource(R.string.dash_disk), stringResource(R.string.dash_free, android.text.format.Formatter.formatShortFileSize(ctx, free)))
            }
        }
    }
}

