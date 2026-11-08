package at.dimes.dmoffice.ui

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.staggeredgrid.LazyVerticalStaggeredGrid
import androidx.compose.foundation.lazy.staggeredgrid.StaggeredGridCells
import androidx.compose.foundation.lazy.staggeredgrid.StaggeredGridItemSpan
import androidx.compose.foundation.lazy.staggeredgrid.items
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Wallpaper
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.Slider
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.dashboard.DashBackground
import at.dimes.dmoffice.dashboard.DashLayout
import at.dimes.dmoffice.dashboard.DashWidget
import at.dimes.dmoffice.dashboard.int
import at.dimes.dmoffice.dashboard.list
import at.dimes.dmoffice.dashboard.str
import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.Thumbs
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import java.time.LocalDate
import java.time.LocalTime

/**
 * Übersicht (Version 2): konfigurierbare Widgets wie im Web - dieselbe Zusammenstellung vom Server.
 * Zwei Spalten; klein = eine Kachel, mittel = ganze Breite, groß = ganze Breite und doppelt hoch, hoch = eine Kachel doppelt hoch.
 * Anpassen: Pfeile verschieben, S/M/L ändert die Größe, ⚙ die Einstellungen, × entfernt.
 */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
fun DashboardScreen(onTab: (Tab) -> Unit, onCalendarDay: (LocalDate) -> Unit, onNavigate: (String) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val snack = remember { SnackbarHostState() }
    val nav = remember { DashNav(onTab, onCalendarDay, onNavigate, snack, scope) }
    var layout by remember { mutableStateOf<DashLayout?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    // Anpassen: Stand von vorher - Änderungen bleiben bis „Fertig“ nur hier, „Abbrechen“ stellt ihn wieder her
    var before by remember { mutableStateOf<DashLayout?>(null) }
    val editing = before != null
    var tick by remember { mutableIntStateOf(0) }
    var adding by remember { mutableStateOf<String?>(null) } // "" = oben, sonst id des Stapels
    var settingsFor by remember { mutableStateOf<DashWidget?>(null) }
    var bgDialog by remember { mutableStateOf(false) }
    var resetAsk by remember { mutableStateOf(false) }
    var menu by remember { mutableStateOf(false) }

    LaunchedEffect(tick) {
        try { layout = withContext(Dispatchers.IO) { app.dashboard.layout() }; error = null }
        catch (e: CancellationException) { throw e } catch (e: Exception) { error = e.message ?: e.toString() }
    }
    // Speichern sofort beim Ändern; der Server prüft und gibt die gültige Zusammenstellung zurück
    fun save(l: DashLayout) {
        layout = l
        if (before != null) return
        scope.launch {
            try { layout = withContext(Dispatchers.IO) { app.dashboard.save(l) } }
            catch (e: CancellationException) { throw e } catch (e: Exception) { snack.showSnackbar(e.message ?: e.toString()) }
        }
    }
    fun patch(f: (List<DashWidget>) -> List<DashWidget>) { layout?.let { save(it.copy(widgets = f(it.widgets))) } }
    fun update(id: String, f: (DashWidget) -> DashWidget) = patch { l -> l.map { if (it.id == id) f(it) else it.copy(stack = it.stack?.map { s -> if (s.id == id) f(s) else s }) } }
    fun newId() = java.util.UUID.randomUUID().toString().replace("-", "").take(8)

    val bmp by produceState<Bitmap?>(null, layout?.background, tick) { value = layout?.background?.let { loadBackground(ctx, it) } }
    val bg = layout?.background

    Scaffold(
        contentWindowInsets = WindowInsets(0),
        snackbarHost = { SnackbarHost(snack) },
        topBar = {
            TabTopBar(stringResource(R.string.dashboard), insets = WindowInsets(0), actions = {
                if (editing) {
                    IconButton(onClick = { adding = "" }) { Icon(Icons.Default.Add, contentDescription = stringResource(R.string.dash_add)) }
                    IconButton(onClick = { bgDialog = true }) { Icon(Icons.Default.Wallpaper, contentDescription = stringResource(R.string.dash_background)) }
                    IconButton(onClick = { before = null; layout?.let { save(it) } }) { Icon(Icons.Default.Check, contentDescription = stringResource(R.string.dash_done)) }
                    IconButton(onClick = { before?.let { b -> layout = b.copy(background = layout?.background ?: b.background) }; before = null; settingsFor = null; adding = null }) {
                        Icon(Icons.Default.Close, contentDescription = stringResource(R.string.dash_cancel))
                    }
                } else {
                    IconButton(onClick = { tick++ }) { Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.dash_refresh)) }
                    IconButton(onClick = { before = layout }) { Icon(Icons.Default.Edit, contentDescription = stringResource(R.string.dash_customize)) }
                }
                Box {
                    IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = null) }
                    DropdownMenu(menu, { menu = false }) {
                        DropdownMenuItem(text = { Text(stringResource(R.string.dash_background)) }, onClick = { menu = false; bgDialog = true })
                        DropdownMenuItem(text = { Text(stringResource(R.string.dash_reset)) }, onClick = { menu = false; resetAsk = true })
                    }
                }
            })
        },
    ) { pad ->
        Box(Modifier.padding(pad).fillMaxSize()) {
            bmp?.let {
                Image(it.asImageBitmap(), contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
                Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = (bg?.dim ?: 30) / 100f)))
            }
            val l = layout
            when {
                l == null && error != null -> Text(stringResource(R.string.dash_failed, error ?: ""), Modifier.padding(16.dp), color = MaterialTheme.colorScheme.error)
                l == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { androidx.compose.material3.CircularProgressIndicator() }
                // Versetztes Raster: schmale Widgets rücken in die Lücke neben einem hohen nach; breite gehen über beide Spalten
                else -> LazyVerticalStaggeredGrid(StaggeredGridCells.Fixed(2), Modifier.fillMaxSize(), contentPadding = PaddingValues(12.dp),
                    horizontalArrangement = Arrangement.spacedBy(10.dp), verticalItemSpacing = 10.dp) {
                    items(l.widgets, key = { it.id }, span = { if (it.size == 1 || it.size == 4) StaggeredGridItemSpan.SingleLane else StaggeredGridItemSpan.FullLine }) { w ->
                        val i = l.widgets.indexOf(w)
                        WidgetCard(w, glass = bmp != null, editing = editing, tick = tick, nav = nav,
                            onRemove = { patch { list -> list.filter { it.id != w.id } } },
                            onSize = { s -> update(w.id) { it.copy(size = s) } },
                            onMove = { by -> patch { list -> val j = i + by; if (j !in list.indices) list else list.toMutableList().also { m -> val x = m.removeAt(i); m.add(j, x) } } },
                            onSettings = { settingsFor = w }, canUp = i > 0, canDown = i < l.widgets.size - 1)
                    }
                    if (editing) item(span = StaggeredGridItemSpan.FullLine) {
                        OutlinedButton(onClick = { adding = "" }, Modifier.fillMaxWidth().height(56.dp)) { Icon(Icons.Default.Add, null); Spacer(Modifier.width(6.dp)); Text(stringResource(R.string.dash_add)) }
                    }
                }
            }
        }
    }

    if (adding != null) AddWidgetDialog(inStack = adding!!.isNotEmpty(), onPick = { type ->
        val w = DashWidget(newId(), type, WIDGETS[type]?.size ?: 1, opts = JsonObject(emptyMap()),
            stack = if (type == "stack") emptyList() else null, smart = type == "stack")
        val into = adding!!
        if (into.isNotEmpty()) update(into) { it.copy(stack = (it.stack ?: emptyList()) + w.copy(size = 1)) }
        else patch { it + w }
        adding = null
        if (type != "stack" && SETTINGS[type]?.isNotEmpty() == true && into.isEmpty()) settingsFor = w
    }, onDismiss = { adding = null })

    settingsFor?.let { w ->
        // Aktueller Stand (beim Stapel ändern sich die Kinder, während der Dialog offen ist)
        val live = layout?.widgets?.firstOrNull { it.id == w.id } ?: w
        WidgetSettingsDialog(live, onSave = { nw -> update(w.id) { nw.copy(stack = it.stack) }; settingsFor = null }, onDismiss = { settingsFor = null },
            onAddToStack = { adding = w.id }, onRemoveFromStack = { id -> update(w.id) { it.copy(stack = it.stack?.filter { s -> s.id != id }) } })
    }

    if (bgDialog) layout?.let { l ->
        BackgroundDialog(l.background, onDismiss = { bgDialog = false }, onSave = { b, upload ->
            bgDialog = false
            scope.launch {
                try {
                    layout = withContext(Dispatchers.IO) {
                        when {
                            upload != null -> app.dashboard.uploadBackground(upload.first, upload.second).let { app.dashboard.save(it.copy(background = it.background.copy(dim = b.dim))) }
                            b.kind == "none" && l.background.kind == "upload" -> app.dashboard.removeBackground()
                            else -> app.dashboard.save(l.copy(background = b))
                        }
                    }
                    tick++
                } catch (e: CancellationException) { throw e } catch (e: Exception) { snack.showSnackbar(e.message ?: e.toString()) }
            }
        })
    }

    if (resetAsk) AlertDialog(onDismissRequest = { resetAsk = false }, text = { Text(stringResource(R.string.dash_reset_confirm)) },
        confirmButton = { TextButton(onClick = {
            resetAsk = false
            // Zurück auf die Vorgabe: auf dem Handy wieder die Desktop-Fassung
            before = null
            scope.launch { try { layout = withContext(Dispatchers.IO) { app.dashboard.reset() } } catch (e: CancellationException) { throw e } catch (e: Exception) { snack.showSnackbar(e.message ?: e.toString()) } }
        }) { Text(stringResource(R.string.dash_reset)) } },
        dismissButton = { TextButton(onClick = { resetAsk = false }) { Text(stringResource(R.string.dash_cancel)) } })
}

private val IMAGE_EXT = Regex("\\.(jpe?g|png|webp|heic|heif|gif|avif|tiff?|arw|cr[23]|nef|raf|orf|rw2|dng|pef|srw)$", RegexOption.IGNORE_CASE)

/** Hintergrundbild: hochgeladen (vom Server), aus der Galerie (großes Vorschaubild) oder jeden Tag ein anderes aus einem Album. */
private suspend fun loadBackground(ctx: android.content.Context, b: DashBackground): Bitmap? = withContext(Dispatchers.IO) {
    val app = App.get(ctx)
    try {
        when (b.kind) {
            "upload" -> app.dashboard.backgroundBytes()?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }
            "gallery" -> b.path?.let { Thumbs.load(ctx, FileItem(it, it.substringAfterLast('/'), false), Thumbs.LARGE) }
            "album" -> b.album?.let { id ->
                // Fotos am Typ erkennen, sonst an der Endung (aus Galerie-Ordnern fehlt der Typ manchmal)
                val imgs = app.files.albumItems(id).first.filter { it.mime?.startsWith("image/") == true || (it.mime?.startsWith("video/") != true && IMAGE_EXT.containsMatchIn(it.name)) }
                if (imgs.isEmpty()) null else Thumbs.load(ctx, imgs[(LocalDate.now().toEpochDay() % imgs.size).toInt()], Thumbs.LARGE)
            }
            else -> null
        }
    } catch (_: Exception) { null }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun WidgetCard(w: DashWidget, glass: Boolean, editing: Boolean, tick: Int, nav: DashNav, onRemove: () -> Unit, onSize: (Int) -> Unit,
                       onMove: (Int) -> Unit, onSettings: () -> Unit, canUp: Boolean, canDown: Boolean) {
    // Groß (2×2) und hoch (1×2) doppelt so hoch
    val height = if (w.size >= 3) 350.dp else 170.dp
    val surface = MaterialTheme.colorScheme.surfaceContainer
    Card(Modifier.fillMaxWidth().height(height).let { if (editing) it.border(2.dp, MaterialTheme.colorScheme.primary.copy(alpha = .5f), RoundedCornerShape(20.dp)) else it },
        shape = RoundedCornerShape(20.dp),
        colors = CardDefaults.cardColors(containerColor = if (glass) surface.copy(alpha = .82f) else surface)) {
        Box(Modifier.fillMaxSize()) {
            Column(Modifier.fillMaxSize().padding(if (w.type == "photos") 0.dp else 12.dp)) {
                if (w.type == "stack") StackBody(w, tick, nav) else WidgetBody(w, w.size >= 3, tick, nav)
            }
            if (editing) {
                // Bedienleiste über dem Inhalt, Entfernen als erster Knopf darin (darüber gelegt verdeckte er bei schmalen
                // Widgets die Pfeile); reicht die Breite nicht, bricht sie in eine zweite Zeile um
                FlowRow(Modifier.align(Alignment.TopEnd).padding(4.dp).clip(RoundedCornerShape(10.dp)).background(MaterialTheme.colorScheme.surfaceVariant),
                    horizontalArrangement = Arrangement.End, verticalArrangement = Arrangement.Center) {
                    IconButton(onClick = onRemove, modifier = Modifier.size(32.dp)) {
                        Box(Modifier.size(24.dp).clip(CircleShape).background(MaterialTheme.colorScheme.error), contentAlignment = Alignment.Center) {
                            Icon(Icons.Default.Close, stringResource(R.string.dash_remove), tint = MaterialTheme.colorScheme.onError, modifier = Modifier.size(16.dp))
                        }
                    }
                    IconButton(onClick = { onMove(-1) }, enabled = canUp, modifier = Modifier.size(32.dp)) { Icon(Icons.Default.KeyboardArrowUp, stringResource(R.string.dash_up)) }
                    IconButton(onClick = { onMove(1) }, enabled = canDown, modifier = Modifier.size(32.dp)) { Icon(Icons.Default.KeyboardArrowDown, stringResource(R.string.dash_down)) }
                    IconButton(onClick = onSettings, modifier = Modifier.size(32.dp)) { Icon(Icons.Default.Settings, stringResource(R.string.dash_settings), Modifier.size(18.dp)) }
                    // Größen als Gruppe - umbrechen nur als Ganzes
                    Row(Modifier.height(32.dp), verticalAlignment = Alignment.CenterVertically) {
                        for ((s, label) in listOf(1 to R.string.dash_size_s, 2 to R.string.dash_size_m, 4 to R.string.dash_size_h, 3 to R.string.dash_size_l))
                            Text(stringResource(label), Modifier.clip(RoundedCornerShape(6.dp)).background(if (w.size == s) MaterialTheme.colorScheme.primary else Color.Transparent)
                                .clickable { onSize(s) }.padding(horizontal = 7.dp, vertical = 3.dp),
                                color = if (w.size == s) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.labelMedium)
                    }
                }
            }
        }
    }
}

/** Stapel: mehrere Widgets zum Durchwischen; „klug“: je nach Tageszeit das passende vorn (morgens Wetter und Termine, abends Tagebuch). */
@Composable
private fun StackBody(w: DashWidget, tick: Int, nav: DashNav) {
    val list = w.stack ?: emptyList()
    if (list.isEmpty()) { Text(stringResource(R.string.dash_stack_empty), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant); return }
    val start = remember(list.size, w.smart) { if (w.smart) smartIndex(list) else 0 }
    val pager = rememberPagerState(initialPage = start) { list.size }
    Box(Modifier.fillMaxSize()) {
        HorizontalPager(pager, Modifier.fillMaxSize(), key = { list[it].id }) { p -> Column(Modifier.fillMaxSize()) { WidgetBody(list[p], w.size >= 3, tick, nav) } }
        Row(Modifier.align(Alignment.BottomCenter), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            repeat(list.size) { i -> Box(Modifier.size(6.dp).clip(CircleShape).background(if (i == pager.currentPage) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline)) }
        }
    }
}

private fun smartIndex(list: List<DashWidget>): Int {
    val h = LocalTime.now().hour
    val prefer = when {
        h < 10 -> listOf("weather", "today", "events")
        h < 17 -> listOf("tasks", "events", "mail")
        else -> listOf("journal", "meals", "photos")
    }
    return prefer.firstNotNullOfOrNull { p -> list.indexOfFirst { it.type == p }.takeIf { it >= 0 } } ?: 0
}

// ---------------- Widget hinzufügen ----------------

@Composable
private fun AddWidgetDialog(inStack: Boolean, onPick: (String) -> Unit, onDismiss: () -> Unit) {
    val app = App.get(LocalContext.current)
    // System nur für die Verwaltung: der Server sagt es (403 für alle anderen)
    val admin by produceState(false) { value = try { withContext(Dispatchers.IO) { app.dashboard.data("system", null) }; true } catch (_: Exception) { false } }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.dash_add)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                for ((type, info) in WIDGETS) {
                    if (type == "system" && !admin) continue
                    // Release-Liste und Hauptschalter: Tagebuch-Rückblick, Coach und Wasser, Aufgaben, Notizen, Fotos nur mit ihrem Bereich
                    val area = when (type) { "journal" -> "journal"; "coach", "water" -> "coach"; "tasks", "shopping" -> "tasks"; "notes" -> "notes"; "photos" -> "gallery"; else -> null }
                    if (area != null && !at.dimes.dmoffice.data.Features.on(area)) continue
                    if (type == "stack" && inStack) continue
                    ListItem(headlineContent = { Text(stringResource(info.label)) }, supportingContent = { Text(stringResource(info.hint)) },
                        modifier = Modifier.clickable { onPick(type) })
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.dash_cancel)) } })
}

// ---------------- Einstellungen je Widget ----------------

private sealed interface Setting {
    val label: Int
    data class Multi(override val label: Int, val from: String, val source: String) : Setting
    data class One(override val label: Int, val from: String, val source: String, val none: Int = R.string.dash_all) : Setting
    /** labels: eigene Beschriftungen je Wert; ohne: Anzahl Tage */
    data class Choice(override val label: Int, val options: List<Pair<String, String>>, val def: String, val labels: List<Int>? = null) : Setting
    data class Bool(override val label: Int, val def: Boolean = false) : Setting
    data class Num(override val label: Int, val min: Int, val max: Int) : Setting
    data class Txt(override val label: Int, val hint: Int? = null) : Setting
    data class MailMode(override val label: Int) : Setting
}

private val DAYS = listOf("1" to "1", "3" to "3", "7" to "7", "14" to "14")
private val SETTINGS: Map<String, Map<String, Setting>> = mapOf(
    "events" to linkedMapOf("calendars" to Setting.Multi(R.string.dash_s_calendars, "calendars", "events"), "days" to Setting.Choice(R.string.dash_s_period, DAYS, "3"),
        "invites" to Setting.Bool(R.string.dash_s_invites, true)),
    "meals" to linkedMapOf("calendar" to Setting.One(R.string.dash_s_plan, "plans", "meals"), "range" to Setting.Choice(R.string.dash_s_period, listOf("today" to "1", "week" to "7"), "week")),
    "tasks" to linkedMapOf("lists" to Setting.Multi(R.string.dash_s_lists, "lists", "tasks"), "days" to Setting.Choice(R.string.dash_s_period, DAYS, "3"),
        "assigned" to Setting.Bool(R.string.dash_s_assigned), "mail" to Setting.Bool(R.string.dash_s_mail, true)),
    "shopping" to linkedMapOf("list" to Setting.One(R.string.dash_s_list, "lists", "shopping", R.string.dash_first_list)),
    "mail" to linkedMapOf("accounts" to Setting.Multi(R.string.dash_s_accounts, "accounts", "mail"), "mode" to Setting.MailMode(R.string.dash_s_show), "count" to Setting.Num(R.string.dash_s_count, 1, 20)),
    "chat" to linkedMapOf("count" to Setting.Num(R.string.dash_s_count, 1, 20)),
    "news" to linkedMapOf("count" to Setting.Num(R.string.dash_s_count, 1, 15), "text" to Setting.Choice(R.string.dash_s_font, listOf("s" to "s", "m" to "m", "l" to "l"), "m",
        listOf(R.string.dash_font_s, R.string.dash_font_m, R.string.dash_font_l))),
    "journal" to linkedMapOf("journal" to Setting.One(R.string.dash_s_journal, "journals", "journal")),
    "notes" to linkedMapOf("favorites" to Setting.Bool(R.string.dash_s_favorites), "count" to Setting.Num(R.string.dash_s_count, 1, 20)),
    "files" to linkedMapOf("favorites" to Setting.Bool(R.string.dash_s_favorites), "count" to Setting.Num(R.string.dash_s_count, 1, 20)),
    "birthdays" to linkedMapOf("days" to Setting.Choice(R.string.dash_s_period, listOf("7" to "7", "14" to "14", "30" to "30", "90" to "90"), "14")),
    "photos" to linkedMapOf("every" to Setting.Choice(R.string.dash_s_every, listOf("7" to "7", "30" to "30", "60" to "60", "300" to "300", "0" to "0"), "7",
        listOf(R.string.dash_every_7, R.string.dash_every_30, R.string.dash_every_60, R.string.dash_every_300, R.string.dash_every_0))),
    "countdown" to linkedMapOf("title" to Setting.Txt(R.string.dash_s_word, R.string.dash_s_word_hint)),
)

private fun JsonObject.prim(k: String): JsonPrimitive? = this[k] as? JsonPrimitive

@Composable
private fun WidgetSettingsDialog(w: DashWidget, onSave: (DashWidget) -> Unit, onDismiss: () -> Unit, onAddToStack: () -> Unit, onRemoveFromStack: (String) -> Unit) {
    val app = App.get(LocalContext.current)
    var title by remember { mutableStateOf(w.title ?: "") }
    var opts by remember { mutableStateOf(w.opts ?: JsonObject(emptyMap())) }
    var smart by remember { mutableStateOf(w.smart) }
    val defs = SETTINGS[w.type] ?: emptyMap()
    // Auswahllisten (Kalender, Listen, Konten …) aus den Daten des Widgets selbst
    val lists = remember { mutableStateMapOf<String, List<Pair<String, String>>>() }
    var cats by remember { mutableStateOf<List<Pair<String, String>>>(emptyList()) }
    LaunchedEffect(Unit) {
        for (d in defs.values) {
            val (from, source) = when (d) { is Setting.Multi -> d.from to d.source; is Setting.One -> d.from to d.source; else -> continue }
            try { lists[from] = withContext(Dispatchers.IO) { app.dashboard.data(source, null) }.list(from).map { (it["id"] as? JsonPrimitive)?.content.orEmpty() to (it.str("name") ?: "") } } catch (_: Exception) { }
        }
        if (w.type == "mail") try {
            cats = withContext(Dispatchers.IO) { val a = app.api!!; (a.json.parseToJsonElement(a.getText(a.url("/api/settings/categories"))) as JsonArray).mapNotNull { it as? JsonObject }.map { (it.str("key") ?: "") to (it.str("name") ?: "") } }
        } catch (_: Exception) { }
    }
    fun set(k: String, v: JsonElement?) { opts = JsonObject(if (v == null) opts - k else opts + (k to v)) }
    fun num(s: String): JsonPrimitive = s.toLongOrNull()?.let { JsonPrimitive(it) } ?: JsonPrimitive(s)

    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.dash_settings) + " – " + (WIDGETS[w.type]?.let { stringResource(it.label) } ?: w.type)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedTextField(title, { title = it }, label = { Text(stringResource(R.string.dash_title)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                for ((k, d) in defs) {
                    when (d) {
                        is Setting.Multi -> Column {
                            Text(stringResource(d.label), style = MaterialTheme.typography.labelLarge)
                            val sel = (opts[k] as? JsonArray)?.mapNotNull { (it as? JsonPrimitive)?.content } ?: emptyList()
                            Row(Modifier.fillMaxWidth().clickable { set(k, JsonArray(emptyList())) }, verticalAlignment = Alignment.CenterVertically) { Checkbox(sel.isEmpty(), { set(k, JsonArray(emptyList())) }); Text(stringResource(R.string.dash_all)) }
                            for ((id, name) in lists[d.from] ?: emptyList()) Row(verticalAlignment = Alignment.CenterVertically) {
                                Checkbox(id in sel, { on -> set(k, JsonArray((if (on) sel + id else sel - id).map { num(it) })) }); Text(name)
                            }
                        }
                        is Setting.One -> Column {
                            Text(stringResource(d.label), style = MaterialTheme.typography.labelLarge)
                            val cur = opts.prim(k)?.contentOrNull
                            Row(verticalAlignment = Alignment.CenterVertically) { RadioButton(cur == null, { set(k, null) }); Text(stringResource(d.none)) }
                            for ((id, name) in lists[d.from] ?: emptyList()) Row(verticalAlignment = Alignment.CenterVertically) { RadioButton(cur == id, { set(k, num(id)) }); Text(name) }
                        }
                        is Setting.Choice -> Column {
                            Text(stringResource(d.label), style = MaterialTheme.typography.labelLarge)
                            val cur = opts.prim(k)?.contentOrNull ?: d.def
                            // Eigene Beschriftungen (länger): untereinander; sonst Tage nebeneinander
                            if (d.labels != null) d.options.forEachIndexed { i, (v, _) ->
                                Row(Modifier.fillMaxWidth().clickable { set(k, num(v)) }, verticalAlignment = Alignment.CenterVertically) { RadioButton(cur == v, { set(k, num(v)) }); Text(stringResource(d.labels[i])) }
                            } else SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                                d.options.forEachIndexed { i, (v, n) ->
                                    SegmentedButton(cur == v, { set(k, num(v)) }, SegmentedButtonDefaults.itemShape(i, d.options.size)) {
                                        Text(if (n == "1") stringResource(R.string.dash_day_1) else stringResource(R.string.dash_days_n, n.toInt()), maxLines = 1)
                                    }
                                }
                            }
                        }
                        is Setting.Bool -> Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                            Text(stringResource(d.label), Modifier.weight(1f))
                            Switch(opts.prim(k)?.booleanOrNull ?: d.def, { set(k, JsonPrimitive(it)) })
                        }
                        is Setting.Num -> OutlinedTextField(opts.prim(k)?.contentOrNull ?: "", { v -> set(k, v.toIntOrNull()?.coerceIn(d.min, d.max)?.let { JsonPrimitive(it) }) },
                            label = { Text(stringResource(d.label)) }, singleLine = true, modifier = Modifier.width(140.dp),
                            keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(keyboardType = androidx.compose.ui.text.input.KeyboardType.Number))
                        is Setting.Txt -> OutlinedTextField(opts.prim(k)?.contentOrNull ?: "", { v -> set(k, v.takeIf { it.isNotEmpty() }?.let { JsonPrimitive(it) }) },
                            label = { Text(stringResource(d.label)) }, supportingText = d.hint?.let { { Text(stringResource(it)) } }, singleLine = true, modifier = Modifier.fillMaxWidth())
                        is Setting.MailMode -> Column {
                            Text(stringResource(d.label), style = MaterialTheme.typography.labelLarge)
                            val cur = opts.prim(k)?.contentOrNull ?: "all"
                            val all = listOf("all" to stringResource(R.string.dash_all_unread), "focused" to stringResource(R.string.dash_relevant)) +
                                cats.map { (key, name) -> "category:$key" to stringResource(R.string.dash_category, name) }
                            for ((v, n) in all) Row(verticalAlignment = Alignment.CenterVertically) { RadioButton(cur == v, { set(k, JsonPrimitive(v)) }); Text(n) }
                        }
                    }
                }
                if (w.type == "stack") {
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) { Text(stringResource(R.string.dash_smart), Modifier.weight(1f)); Switch(smart, { smart = it }) }
                    Text(stringResource(R.string.dash_in_stack), style = MaterialTheme.typography.labelLarge)
                    val children = w.stack ?: emptyList()
                    if (children.isEmpty()) Text(stringResource(R.string.dash_stack_empty), style = MaterialTheme.typography.bodySmall)
                    for (c in children) Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Text(widgetLabel(c), Modifier.weight(1f)); TextButton(onClick = { onRemoveFromStack(c.id) }) { Text(stringResource(R.string.dash_out_of_stack)) }
                    }
                    OutlinedButton(onClick = onAddToStack) { Icon(Icons.Default.Add, null); Text(stringResource(R.string.dash_into_stack)) }
                }
            }
        },
        confirmButton = { TextButton(onClick = { onSave(w.copy(title = title.ifBlank { null }, opts = opts, smart = smart)) }) { Text(stringResource(R.string.dash_save)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.dash_cancel)) } })
}

// ---------------- Hintergrund ----------------

@Composable
private fun BackgroundDialog(bg: DashBackground, onDismiss: () -> Unit, onSave: (DashBackground, Pair<ByteArray, String>?) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    var draft by remember { mutableStateOf(bg) }
    var picked by remember { mutableStateOf<Pair<ByteArray, String>?>(null) }
    val albums by produceState<List<at.dimes.dmoffice.files.AlbumInfo>>(emptyList()) { value = try { withContext(Dispatchers.IO) { app.files.albums() } } catch (_: Exception) { emptyList() } }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) try {
            val bytes = ctx.contentResolver.openInputStream(uri)?.use { it.readBytes() }
            if (bytes != null) { picked = bytes to (ctx.contentResolver.getType(uri) ?: "image/jpeg"); draft = draft.copy(kind = "upload") }
        } catch (_: Exception) { }
    }
    var galleryPick by remember { mutableStateOf(false) }
    if (galleryPick) GalleryPhotoPicker(onDismiss = { galleryPick = false }) { items -> galleryPick = false; items.firstOrNull()?.let { draft = draft.copy(kind = "gallery", path = it.path) } }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.dash_background)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                @Composable fun option(kind: String, label: Int) = Row(Modifier.fillMaxWidth().clickable { draft = draft.copy(kind = kind) }, verticalAlignment = Alignment.CenterVertically) {
                    RadioButton(draft.kind == kind, { draft = draft.copy(kind = kind) }); Text(stringResource(label))
                }
                option("none", R.string.dash_bg_none)
                // Ein Foto aus der Galerie von dmOffice (auch aus Galerie-Ordnern): gespeichert wird nur der Pfad
                option("gallery", R.string.dash_bg_gallery)
                if (draft.kind == "gallery") OutlinedButton(onClick = { galleryPick = true }, Modifier.padding(start = 48.dp)) {
                    Text(stringResource(R.string.dash_bg_choose) + if (draft.path != null) " ✓" else "")
                }
                option("upload", R.string.dash_bg_upload)
                if (draft.kind == "upload") OutlinedButton(onClick = { pick.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) }, Modifier.padding(start = 48.dp)) {
                    Text(stringResource(R.string.dash_bg_choose) + if (picked != null) " ✓" else "")
                }
                option("album", R.string.dash_bg_album)
                if (draft.kind == "album") Column(Modifier.padding(start = 32.dp)) {
                    for (a in albums) Row(Modifier.fillMaxWidth().clickable { draft = draft.copy(album = a.id) }, verticalAlignment = Alignment.CenterVertically) {
                        RadioButton(draft.album == a.id, { draft = draft.copy(album = a.id) }); Text(a.name)
                    }
                }
                if (draft.kind != "none") {
                    Text(stringResource(R.string.dash_bg_dim, draft.dim), style = MaterialTheme.typography.labelLarge, modifier = Modifier.padding(top = 8.dp))
                    Slider(draft.dim.toFloat(), { draft = draft.copy(dim = (it / 5).toInt() * 5) }, valueRange = 0f..80f)
                }
            }
        },
        confirmButton = { TextButton(onClick = { onSave(draft, if (draft.kind == "upload") picked else null) }) { Text(stringResource(R.string.dash_save)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.dash_cancel)) } })
}
