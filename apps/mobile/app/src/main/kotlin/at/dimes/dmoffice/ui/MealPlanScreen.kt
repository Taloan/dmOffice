package at.dimes.dmoffice.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.scrollBy
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.unit.IntOffset
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.filled.MenuBook
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ShoppingCart
import androidx.compose.material.icons.filled.Today
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.ListTypes
import at.dimes.dmoffice.sync.SyncWorker
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.time.DayOfWeek
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.time.temporal.TemporalAdjusters

/** Mahlzeiten wie im Web: der Schluessel steht als Kategorie im Termin (sprachunabhaengig), die Uhrzeit nur fuer andere Programme. */
private enum class Meal(val key: String, val label: Int, val hour: Int) {
    Breakfast("Frühstück", R.string.meal_breakfast, 8), Lunch("Mittagessen", R.string.meal_lunch, 12), Dinner("Abendessen", R.string.meal_dinner, 18)
}

@Serializable private data class PlanCal(val id: Long, val name: String = "", val color: String? = null, val calendarKind: String? = null,
                                         val owner: String? = null, val canWrite: Boolean = true, val subscription: kotlinx.serialization.json.JsonElement? = null,
                                         val listType: String? = null)
@Serializable private data class PlanEvent(val id: String = "", val calendar: Long, val uri: String, val etag: String = "", val title: String = "",
                                           val start: String, val end: String, val categories: List<String> = emptyList(), val recipe: String? = null)
@Serializable private data class Recipe(val id: Long = 0, val syncId: String = "", val title: String = "", val folder: String? = null, val db: String? = null)
@Serializable private data class Ingredients(val syncId: String = "", val title: String = "", val items: List<String> = emptyList())

private fun PlanCal.label(ctx: android.content.Context) = if (owner != null) ctx.getString(R.string.shared_from, name, owner) else name
private fun PlanEvent.meal() = Meal.entries.firstOrNull { it.key in categories }
private fun PlanEvent.day(zone: ZoneId): LocalDate = Instant.parse(start).atZone(zone).toLocalDate()
private fun colorOf(hex: String?) = try { Color(android.graphics.Color.parseColor(hex ?: "#0F6CBD")) } catch (_: Exception) { Color(0xFF0F6CBD) }

private const val PREFS = "mealplan"

/** Datum in der Schreibweise der Spracheinstellung ("19. Okt." bzw. "Oct 19"). */
private fun fmt(d: LocalDate, skeleton: String): String =
    d.format(DateTimeFormatter.ofPattern(android.text.format.DateFormat.getBestDateTimePattern(java.util.Locale.getDefault(), skeleton)))

/**
 * Essensplan wie im Web: Woche mit Fruehstueck, Mittag und Abend aus allen Kalendern der Art Essensplan (auch geteilten), jeder Eintrag
 * in der Farbe seines Kalenders. Eintraege sind gewoehnliche Termine - Freitext oder Rezept aus den Notizen. Braucht Verbindung zum Server.
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun MealPlanScreen(onOpenNote: (Long) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val zone = ZoneId.systemDefault()
    val today = LocalDate.now(zone)
    var week by remember { mutableStateOf(today.with(TemporalAdjusters.previousOrSame(DayOfWeek.MONDAY))) }
    var cals by remember { mutableStateOf<List<PlanCal>?>(null) }
    var events by remember { mutableStateOf<List<PlanEvent>>(emptyList()) }
    var recipes by remember { mutableStateOf<List<Recipe>>(emptyList()) }
    var error by remember { mutableStateOf<String?>(null) }
    var reload by remember { mutableIntStateOf(0) }
    var edit by remember { mutableStateOf<Triple<LocalDate, Meal, PlanEvent?>?>(null) }
    var shopping by remember { mutableStateOf(false) }
    // Halten und ziehen: welcher Eintrag, wo der Finger ist (Fensterkoordinaten), wo die Felder liegen
    var dragging by remember { mutableStateOf<PlanEvent?>(null) }
    var pointer by remember { mutableStateOf(Offset.Zero) }
    val slots = remember { mutableStateMapOf<Pair<LocalDate, Meal>, Rect>() }
    val listState = rememberLazyListState()
    var box by remember { mutableStateOf(Rect.Zero) }
    val haptic = LocalHapticFeedback.current
    val density = LocalDensity.current
    fun target() = slots.entries.firstOrNull { it.value.contains(pointer) }?.key

    LaunchedEffect(Unit) {
        withContext(Dispatchers.IO) {
            val api = app.api ?: return@withContext
            try { recipes = api.get(api.url("/api/notes/recipes")) } catch (_: Exception) { }
        }
    }
    LaunchedEffect(week, reload) {
        withContext(Dispatchers.IO) {
            val api = app.api ?: return@withContext
            try {
                val all: List<PlanCal> = api.get(api.url("/api/pim/collections/calendars"))
                val mine = all.filter { it.calendarKind == "meal" && it.subscription == null }
                cals = mine
                val from = week.atStartOfDay(zone).toInstant().toString()
                val to = week.plusDays(7).atStartOfDay(zone).toInstant().toString()
                events = if (mine.isEmpty()) emptyList() else api.get<List<PlanEvent>>(api.url("/api/pim/events", "from" to from, "to" to to,
                    "calendars" to mine.joinToString(",") { it.id.toString() })).filter { it.meal() != null }
                error = null
            } catch (e: Exception) { error = e.message ?: e.javaClass.simpleName }
        }
    }
    val writable = cals.orEmpty().filter { it.canWrite }
    fun calOf(id: Long) = cals?.firstOrNull { it.id == id }

    /** Auf einen anderen Tag oder eine andere Mahlzeit gezogen: sofort hier zeigen, dann beim Server speichern (wie im Web). */
    fun moveTo(e: PlanEvent, d: LocalDate, m: Meal) {
        if (e.day(zone) == d && e.meal() == m) return
        fun at(h: Int) = d.atTime(h, 0).atZone(zone).toInstant().toString()
        val moved = e.copy(start = at(m.hour), end = at(m.hour + 1), categories = listOf(m.key))
        events = events.map { if (it.uri == e.uri && it.calendar == e.calendar) moved else it }
        scope.launch {
            try {
                withContext(Dispatchers.IO) {
                    val api = app.api ?: error("offline")
                    val body = buildJsonObject {
                        put("calendar", e.calendar); put("uri", e.uri); put("etag", e.etag); put("title", e.title)
                        put("start", moved.start); put("end", moved.end); put("allDay", false); put("busy", false)
                        put("categories", JsonArray(listOf(JsonPrimitive(m.key)))); put("reminder", JsonNull)
                        put("recipe", e.recipe ?: ""); put("scope", "series")
                    }
                    api.postText(api.url("/api/pim/events"), body.toString())
                }
                SyncWorker.now(ctx)
            } catch (x: Exception) { error = x.message ?: x.javaClass.simpleName }
            reload++
        }
    }
    // Am oberen oder unteren Rand halten: die Liste laeuft mit
    LaunchedEffect(dragging) {
        val edge = with(density) { 72.dp.toPx() }
        while (dragging != null) {
            val speed = when { pointer.y < box.top + edge -> -18f; pointer.y > box.bottom - edge -> 18f; else -> 0f }
            if (speed != 0f) listState.scrollBy(speed)
            kotlinx.coroutines.delay(16)
        }
    }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { week = week.minusWeeks(1) }) { Icon(Icons.AutoMirrored.Filled.KeyboardArrowLeft, contentDescription = stringResource(R.string.meal_prev_week)) }
            Text(fmt(week, "dMMM") + " – " + fmt(week.plusDays(6), "dMMMyyyy"),
                modifier = Modifier.weight(1f), textAlign = TextAlign.Center, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            IconButton(onClick = { week = week.plusWeeks(1) }) { Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = stringResource(R.string.meal_next_week)) }
            IconButton(onClick = { week = today.with(TemporalAdjusters.previousOrSame(DayOfWeek.MONDAY)) }) { Icon(Icons.Default.Today, contentDescription = stringResource(R.string.meal_this_week)) }
            IconButton(onClick = { shopping = true }, enabled = events.any { !it.recipe.isNullOrEmpty() }) {
                Icon(Icons.Default.ShoppingCart, contentDescription = stringResource(R.string.meal_ingredients))
            }
        }
        error?.let { Text(stringResource(R.string.meal_offline, it), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp)) }
        val list = cals
        when {
            list == null -> {}
            list.isEmpty() -> Text(stringResource(R.string.meal_none), modifier = Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
            else -> Box(Modifier.fillMaxSize().onGloballyPositioned { box = it.boundsInRoot() }) {
              LazyColumn(Modifier.fillMaxSize(), state = listState, contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 88.dp)) {
                items((0..6).map { week.plusDays(it.toLong()) }, key = { it.toString() }) { d ->
                    val isToday = d == today
                    Column(Modifier.fillMaxWidth().background(if (isToday) MaterialTheme.colorScheme.secondaryContainer.copy(alpha = 0.4f) else Color.Transparent)
                        .padding(horizontal = 16.dp, vertical = 6.dp)) {
                        Text(fmt(d, "EEEEdMMMM"), fontWeight = FontWeight.SemiBold,
                            color = if (isToday) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface)
                        for (m in Meal.entries) {
                            val here = events.filter { it.meal() == m && it.day(zone) == d }
                            val over = dragging != null && target() == (d to m)
                            Row(Modifier.fillMaxWidth().heightIn(min = 40.dp).onGloballyPositioned { slots[d to m] = it.boundsInRoot() }
                                .then(if (over) Modifier.border(2.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(6.dp)) else Modifier)
                                .padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                                Text(stringResource(m.label), modifier = Modifier.width(96.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                FlowRow(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(4.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                                    for (e in here) key(e.calendar, e.uri) {
                                      var origin by remember { mutableStateOf(Offset.Zero) }
                                      val movable = calOf(e.calendar)?.canWrite == true
                                      Row(Modifier.onGloballyPositioned { origin = it.boundsInRoot().topLeft }
                                        .then(if (movable) Modifier.pointerInput(e.uri) {
                                            detectDragGesturesAfterLongPress(
                                                onDragStart = { o -> pointer = origin + o; dragging = e; haptic.performHapticFeedback(HapticFeedbackType.LongPress) },
                                                onDrag = { ch, amount -> ch.consume(); pointer += amount },
                                                onDragEnd = { val t = target(); val ev = dragging; dragging = null; if (ev != null && t != null) moveTo(ev, t.first, t.second) },
                                                onDragCancel = { dragging = null },
                                            )
                                        } else Modifier)
                                        .alpha(if (dragging?.uri == e.uri) 0.35f else 1f)
                                        .clip(RoundedCornerShape(6.dp)).background(colorOf(calOf(e.calendar)?.color))
                                        .clickable { edit = Triple(d, m, e) }.padding(horizontal = 8.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                                        if (!e.recipe.isNullOrEmpty()) { Icon(Icons.AutoMirrored.Filled.MenuBook, null, Modifier.size(14.dp), tint = Color.White); Spacer(Modifier.width(4.dp)) }
                                        Text(e.title, color = Color.White, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                      }
                                    }
                                }
                                if (writable.isNotEmpty()) IconButton(onClick = { edit = Triple(d, m, null) }, modifier = Modifier.size(36.dp)) {
                                    Icon(Icons.Default.Add, contentDescription = stringResource(R.string.meal_add), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                                }
                            }
                        }
                    }
                    HorizontalDivider()
                }
                if (writable.isEmpty()) item { Text(stringResource(R.string.meal_read_only), modifier = Modifier.padding(16.dp), style = MaterialTheme.typography.bodySmall) }
              }
              // Der gezogene Eintrag folgt dem Finger
              dragging?.let { e ->
                  Text(e.title, color = Color.White, style = MaterialTheme.typography.bodyMedium, maxLines = 1,
                      modifier = Modifier.offset { IntOffset((pointer.x - box.left - 24.dp.toPx()).toInt(), (pointer.y - box.top - 28.dp.toPx()).toInt()) }
                          .shadow(6.dp, RoundedCornerShape(6.dp)).clip(RoundedCornerShape(6.dp)).background(colorOf(calOf(e.calendar)?.color))
                          .padding(horizontal = 10.dp, vertical = 8.dp))
              }
            }
        }
    }

    edit?.let { (d, m, ev) ->
        MealDialog(d, m, ev, if (ev != null) listOfNotNull(calOf(ev.calendar)) else writable, recipes,
            noteId = ev?.recipe?.let { r -> recipes.firstOrNull { it.syncId == r }?.id?.takeIf { it > 0 } },
            onOpenNote = { edit = null; onOpenNote(it) },
            onDone = { changed -> edit = null; if (changed) { reload++; SyncWorker.now(ctx) } })
    }
    if (shopping) IngredientsDialog(events.filter { !it.recipe.isNullOrEmpty() }) { shopping = false }
}

@Composable
private fun MealDialog(day: LocalDate, meal: Meal, ev: PlanEvent?, targets: List<PlanCal>, recipes: List<Recipe>, noteId: Long?,
                       onOpenNote: (Long) -> Unit, onDone: (Boolean) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val zone = ZoneId.systemDefault()
    val prefs = remember { ctx.getSharedPreferences(PREFS, android.content.Context.MODE_PRIVATE) }
    var title by remember { mutableStateOf(ev?.title ?: "") }
    var recipe by remember { mutableStateOf(ev?.recipe ?: "") }
    // Neuer Eintrag: zuletzt benutzter Kalender, sonst der erste beschreibbare; bestehender bleibt in seinem
    var target by remember { mutableStateOf(targets.firstOrNull { it.id == prefs.getLong("calendar", 0) } ?: targets.firstOrNull()) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val ro = target?.canWrite != true
    val hits = remember(title, recipes) { val w = title.trim().lowercase(); recipes.filter { w.isEmpty() || it.title.lowercase().contains(w) }.take(6) }

    fun save() {
        val t = target ?: return
        if (title.isBlank()) return
        busy = true
        scope.launch {
            try {
                withContext(Dispatchers.IO) {
                    val api = app.api ?: error("offline")
                    fun at(h: Int) = day.atTime(h, 0).atZone(zone).toInstant().toString()
                    val body = buildJsonObject {
                        put("calendar", t.id)
                        if (ev != null) { put("uri", ev.uri); put("etag", ev.etag) }
                        put("title", title.trim())
                        put("start", ev?.start ?: at(meal.hour)); put("end", ev?.end ?: at(meal.hour + 1))
                        put("allDay", false); put("busy", false)
                        put("categories", JsonArray(listOf(JsonPrimitive(meal.key)))); put("reminder", JsonNull)
                        put("recipe", recipe); put("scope", "series")
                    }
                    api.postText(api.url("/api/pim/events"), body.toString())
                }
                if (ev == null) prefs.edit().putLong("calendar", t.id).apply()
                onDone(true)
            } catch (e: Exception) { error = e.message ?: e.javaClass.simpleName } finally { busy = false }
        }
    }
    fun remove() {
        val e = ev ?: return
        scope.launch {
            try {
                withContext(Dispatchers.IO) { val api = app.api ?: error("offline"); api.delete(api.url("/api/pim/events/${e.calendar}/" + android.net.Uri.encode(e.uri))) }
                onDone(true)
            } catch (x: Exception) { error = x.message ?: x.javaClass.simpleName }
        }
    }

    AlertDialog(
        onDismissRequest = { onDone(false) },
        title = { Text(stringResource(meal.label) + " · " + fmt(day, "EEEEdMMMM")) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                OutlinedTextField(title, { title = it; recipe = "" }, label = { Text(stringResource(R.string.meal_what)) }, enabled = !ro, singleLine = true, modifier = Modifier.fillMaxWidth())
                if (ev == null && targets.size > 1) {
                    Text(stringResource(R.string.meal_calendar), style = MaterialTheme.typography.labelMedium)
                    for (c in targets) Row(Modifier.fillMaxWidth().clickable { target = c }, verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(selected = target?.id == c.id, onClick = { target = c }); Text(c.label(ctx))
                    }
                }
                if (!ro) for (r in hits) Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(6.dp))
                    .background(if (recipe == r.syncId) MaterialTheme.colorScheme.secondaryContainer else Color.Transparent)
                    .clickable { title = r.title; recipe = r.syncId }.padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.AutoMirrored.Filled.MenuBook, null, Modifier.size(16.dp)); Spacer(Modifier.width(8.dp))
                    Text(r.title + (r.folder?.let { " · " + it.split(" / ").last() } ?: "") + (r.db?.let { " · $it" } ?: ""), maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                if (recipes.isEmpty() && !ro) Text(stringResource(R.string.meal_no_recipes), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (noteId != null) TextButton(onClick = { onOpenNote(noteId) }) { Text(stringResource(R.string.meal_open_recipe)) }
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = { if (!ro) TextButton(onClick = ::save, enabled = !busy && title.isNotBlank()) { Text(stringResource(R.string.save)) } },
        dismissButton = {
            Row {
                if (ev != null && !ro) TextButton(onClick = ::remove) { Text(stringResource(R.string.delete), color = MaterialTheme.colorScheme.error) }
                TextButton(onClick = { onDone(false) }) { Text(stringResource(if (ro) R.string.close else R.string.cancel)) }
            }
        },
    )
}

/** Zutaten der Rezepte dieser Woche zum Abhaken, auf eine Einkaufsliste (wie im Web). */
@Composable
private fun IngredientsDialog(events: List<PlanEvent>, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var lists by remember { mutableStateOf<List<PlanCal>?>(null) }
    var listId by remember { mutableStateOf<Long?>(null) }
    var groups by remember { mutableStateOf<List<Pair<String, List<Pair<String, Boolean>>>>?>(null) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) {
        withContext(Dispatchers.IO) {
            val api = app.api ?: return@withContext
            try {
                val shop = api.get<List<PlanCal>>(api.url("/api/pim/collections/calendars")).filter { it.listType == "shop" && it.canWrite }
                lists = shop; listId = shop.firstOrNull()?.id
                val ids = events.mapNotNull { it.recipe }.distinct()
                val body = buildJsonObject { put("ids", JsonArray(ids.map { JsonPrimitive(it) })) }
                val found: List<Ingredients> = api.json.decodeFromString(api.postText(api.url("/api/notes/recipes/ingredients"), body.toString()))
                // Ein Rezept, das mehrmals in der Woche auf dem Plan steht, kommt mehrmals auf die Liste
                groups = events.mapNotNull { e -> found.firstOrNull { it.syncId == e.recipe }?.let { r -> r.title to r.items.map { it to true } } }
            } catch (e: Exception) { error = e.message ?: e.javaClass.simpleName }
        }
    }
    val chosen = groups.orEmpty().flatMap { g -> g.second.filter { it.second }.map { it.first } }
    fun add() {
        val id = listId ?: return
        busy = true
        scope.launch {
            try {
                val n = withContext(Dispatchers.IO) {
                    val api = app.api ?: error("offline")
                    val sections = try { api.get<ListMeta>(api.url("/api/pim/lists/$id")).sections } catch (_: Exception) { emptyList() }
                    for (text in chosen) {
                        val x = ListTypes.parseItem(text)
                        val body = buildJsonObject {
                            put("list", id); put("title", x.title()); put("item", x.item); put("qty", x.qty); put("unit", x.unit)
                            ListTypes.guessSection(x.item, sections)?.let { put("section", it) }
                            put("dueAllDay", true)
                        }
                        api.postText(api.url("/api/pim/tasks"), body.toString())
                    }
                    chosen.size
                }
                android.widget.Toast.makeText(ctx, ctx.resources.getQuantityString(R.plurals.meal_added, n, n), android.widget.Toast.LENGTH_SHORT).show()
                SyncWorker.now(ctx)
                onClose()
            } catch (e: Exception) { error = e.message ?: e.javaClass.simpleName } finally { busy = false }
        }
    }
    AlertDialog(
        onDismissRequest = onClose,
        title = { Text(stringResource(R.string.meal_ingredients)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                val l = lists
                if (l != null && l.isEmpty()) Text(stringResource(R.string.meal_no_shop_list))
                if (l != null && l.size > 1) for (c in l) Row(Modifier.fillMaxWidth().clickable { listId = c.id }, verticalAlignment = Alignment.CenterVertically) {
                    RadioButton(selected = listId == c.id, onClick = { listId = c.id }); Text(c.label(ctx))
                }
                if (l != null && l.size == 1) Text(stringResource(R.string.meal_on_list, l[0].label(ctx)), style = MaterialTheme.typography.bodySmall)
                val g = groups
                if (g != null && g.isEmpty()) Text(stringResource(R.string.meal_no_ingredients))
                g?.forEachIndexed { a, (title, items) ->
                    Text(title, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 8.dp))
                    items.forEachIndexed { b, (text, on) ->
                        Row(Modifier.fillMaxWidth().clickable {
                            groups = g.mapIndexed { i, x -> if (i != a) x else x.first to x.second.mapIndexed { j, y -> if (j == b) y.first to !on else y } }
                        }, verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(checked = on, onCheckedChange = null); Spacer(Modifier.width(8.dp)); Text(text)
                        }
                    }
                }
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = { TextButton(onClick = ::add, enabled = !busy && listId != null && chosen.isNotEmpty()) { Text(stringResource(R.string.meal_add_to_list)) } },
        dismissButton = { TextButton(onClick = onClose) { Text(stringResource(R.string.cancel)) } },
    )
}

@Serializable private data class ListMeta(val sections: List<String> = emptyList())
