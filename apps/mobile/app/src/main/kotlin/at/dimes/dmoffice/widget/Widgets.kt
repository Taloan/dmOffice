package at.dimes.dmoffice.widget

import android.appwidget.AppWidgetManager
import android.content.Context
import android.content.Intent
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.ColorFilter
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.action.ActionParameters
import androidx.glance.action.actionParametersOf
import androidx.glance.action.clickable
import androidx.glance.appwidget.CheckBox
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetManager
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.appWidgetBackground
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.lazy.LazyColumn
import androidx.glance.appwidget.lazy.items
import androidx.glance.appwidget.provideContent
import androidx.glance.appwidget.updateAll
import androidx.glance.background
import androidx.glance.color.ColorProvider
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextAlign
import androidx.glance.text.TextDecoration
import androidx.glance.text.TextStyle
import androidx.glance.unit.ColorProvider
import at.dimes.dmoffice.App
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.ColorMark
import at.dimes.dmoffice.data.Content
import at.dimes.dmoffice.data.DayHeader
import at.dimes.dmoffice.data.ItemEntity
import at.dimes.dmoffice.data.MonthGrid
import at.dimes.dmoffice.data.MonthMark
import at.dimes.dmoffice.data.MonthState
import at.dimes.dmoffice.data.MultiDayRule
import at.dimes.dmoffice.data.Occurrence
import at.dimes.dmoffice.data.RowLayout
import at.dimes.dmoffice.data.TimePos
import at.dimes.dmoffice.data.WidgetConfig
import at.dimes.dmoffice.data.WidgetPrefs
import at.dimes.dmoffice.ui.Fmt
import java.time.LocalDate
import java.time.YearMonth
import java.time.ZoneId
import java.time.temporal.WeekFields
import java.util.Locale

object Widgets {
    suspend fun updateAll(context: Context) {
        try {
            AgendaWidget().updateAll(context)
            TasksWidget().updateAll(context)
            MonthWidget().updateAll(context)
        } catch (_: Exception) { }
    }

    fun configIntent(context: Context, appWidgetId: Int, kind: String): Intent =
        Intent(context, WidgetConfigActivity::class.java)
            .putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, appWidgetId)
            .putExtra(WidgetConfigActivity.EXTRA_WIDGET_KIND, kind)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            .setData(android.net.Uri.parse("dmoffice://widget/$kind/$appWidgetId"))

    /** Widget der Art [kind] neu zeichnen (nach Einstellungen oder Tageswahl). */
    suspend fun update(context: Context, glanceId: GlanceId, kind: String) {
        when (kind) {
            WidgetConfigActivity.KIND_TASKS -> TasksWidget().update(context, glanceId)
            WidgetConfigActivity.KIND_MONTH -> MonthWidget().update(context, glanceId)
            else -> AgendaWidget().update(context, glanceId)
        }
    }
}

private val hrefKey = ActionParameters.Key<String>("href")
private val doneKey = ActionParameters.Key<Boolean>("done")
private val tapKey = ActionParameters.Key<Boolean>("tap")
private val dateKey = ActionParameters.Key<String>("date")
private val kindKey = ActionParameters.Key<String>("kind")
private val deltaKey = ActionParameters.Key<Int>("delta")
private val accKey = ActionParameters.Key<String>("acc")
private val folderKey = ActionParameters.Key<String>("folder")
private val uidKey = ActionParameters.Key<Long>("uid")

/** E-Mail-Nachverfolgung im Aufgaben-Widget: gekennzeichnete Mails vom Server, zwischengespeichert fuer offline. */
internal object WidgetFollowUps {
    /** Steht in WidgetConfig.lists wie eine Aufgabenliste. */
    const val HREF = "dmoffice:mail-followups"
    const val COLOR = 0xFFD13438.toInt()
    private val json = kotlinx.serialization.json.Json { ignoreUnknownKeys = true }
    private fun prefs(context: Context) = context.getSharedPreferences("widget_followups", Context.MODE_PRIVATE)

    suspend fun load(context: Context): List<at.dimes.dmoffice.api.FollowUp> {
        val fresh = try { kotlinx.coroutines.withTimeoutOrNull(8000) { App.get(context).mail.followUps() } } catch (_: Exception) { null }
        // Postfaecher (nur bei mehreren Konten angezeigt), ebenfalls zwischengespeichert
        try {
            kotlinx.coroutines.withTimeoutOrNull(5000) { App.get(context).mail.accounts() }?.let { a ->
                prefs(context).edit().putString("mailboxes", json.encodeToString(if (a.size > 1) a.associate { it.id to it.email } else emptyMap())).apply()
            }
        } catch (_: Exception) { }
        if (fresh != null) { prefs(context).edit().putString("list", json.encodeToString(fresh)).apply(); return fresh }
        return try { prefs(context).getString("list", null)?.let { json.decodeFromString<List<at.dimes.dmoffice.api.FollowUp>>(it) } ?: emptyList() } catch (_: Exception) { emptyList() }
    }

    fun mailboxes(context: Context): Map<String, String> =
        try { prefs(context).getString("mailboxes", null)?.let { json.decodeFromString<Map<String, String>>(it) } ?: emptyMap() } catch (_: Exception) { emptyMap() }

    fun drop(context: Context, account: String, folder: String, uid: Long) {
        val p = prefs(context)
        val list = try { p.getString("list", null)?.let { json.decodeFromString<List<at.dimes.dmoffice.api.FollowUp>>(it) } } catch (_: Exception) { null } ?: return
        p.edit().putString("list", json.encodeToString(list.filterNot { it.mail.account == account && it.mail.folder == folder && it.mail.uid == uid })).apply()
    }
}

/** Gekennzeichnete Mail im Widget als erledigt markieren. */
class FollowUpDoneAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        val acc = parameters[accKey] ?: return
        val folder = parameters[folderKey] ?: return
        val uid = parameters[uidKey] ?: return
        WidgetFollowUps.drop(context, acc, folder, uid)
        Widgets.update(context, glanceId, WidgetConfigActivity.KIND_TASKS)
        try { App.get(context).mail.followUp(acc, folder, listOf(uid), null, done = true) } catch (_: Exception) { }
        Widgets.update(context, glanceId, WidgetConfigActivity.KIND_TASKS)
    }
}

/** Abhaken direkt im Widget: lokal setzen, Sync anstossen, Widgets neu zeichnen. */
class ToggleTaskAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        val href = parameters[hrefKey] ?: return
        val done = parameters[doneKey] ?: true
        // Einkaufsliste: erst durchgestrichen stehen lassen, nach kurzer Zeit ausblenden, Rueckgaengig in der Titelleiste
        val id = if (parameters[tapKey] == true) try { GlanceAppWidgetManager(context).getAppWidgetId(glanceId) } catch (_: Exception) { null } else null
        if (id != null) { val p = WidgetPrefs(context); if (done) p.pushDone(id, href) else p.removeDone(id, href) }
        App.get(context).repo.setCompleted(href, done)
        if (id != null && done) {
            Widgets.update(context, glanceId, WidgetConfigActivity.KIND_TASKS)
            kotlinx.coroutines.delay(STRIKE_MS + 200)
            Widgets.update(context, glanceId, WidgetConfigActivity.KIND_TASKS)
        }
    }
}

/** Wie lange ein abgehakter Eintrag im Einkaufslisten-Widget durchgestrichen stehen bleibt. */
internal const val STRIKE_MS = 3000L

/** Rueckgaengig in der Titelleiste: den zuletzt im Widget abgehakten Eintrag wieder oeffnen. */
class UndoDoneAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        val id = try { GlanceAppWidgetManager(context).getAppWidgetId(glanceId) } catch (_: Exception) { return }
        val href = WidgetPrefs(context).popDone(id) ?: return
        App.get(context).repo.setCompleted(href, false)
        Widgets.update(context, glanceId, WidgetConfigActivity.KIND_TASKS)
    }
}

/** Tag im Monatsraster antippen: wird der gewaehlte Tag, Monat folgt dem Tag. */
class SelectDayAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        val date = try { LocalDate.parse(parameters[dateKey]) } catch (_: Exception) { return }
        val id = try { GlanceAppWidgetManager(context).getAppWidgetId(glanceId) } catch (_: Exception) { return }
        WidgetPrefs(context).saveMonth(id, MonthState(YearMonth.from(date), date))
        Widgets.update(context, glanceId, parameters[kindKey] ?: WidgetConfigActivity.KIND_MONTH)
    }
}

/** Monat vor/zurueck (delta +-1) oder zurueck zu heute (delta 0). */
class MonthNavAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        val id = try { GlanceAppWidgetManager(context).getAppWidgetId(glanceId) } catch (_: Exception) { return }
        val prefs = WidgetPrefs(context)
        val today = LocalDate.now()
        val delta = parameters[deltaKey] ?: 0
        val cur = prefs.loadMonth(id, today)
        val next = if (delta == 0) MonthState(YearMonth.from(today), today) else {
            val m = cur.month.plusMonths(delta.toLong())
            // Gewaehlter Tag wandert mit, damit die Liste zum sichtbaren Monat passt
            val sel = if (m == YearMonth.from(today)) today else m.atDay(minOf(cur.selected.dayOfMonth, m.lengthOfMonth()))
            MonthState(m, sel)
        }
        prefs.saveMonth(id, next)
        Widgets.update(context, glanceId, parameters[kindKey] ?: WidgetConfigActivity.KIND_MONTH)
    }
}

internal fun colorOf(c: Int?): ColorProvider = ColorProvider(if (c != null) Color(c) else Color(at.dimes.dmoffice.ui.DEFAULT_ACCENT.toInt()))

/** Hintergrund mit einstellbarer Deckkraft, hell und dunkel. */
private fun background(opacity: Int): ColorProvider {
    val a = opacity.coerceIn(0, 100) / 100f
    return ColorProvider(day = Color(0xFFFFFFFF).copy(alpha = a), night = at.dimes.dmoffice.ui.anthracite(0x1C).copy(alpha = a))
}

/** Sync aus der Kopfzeile des Widgets. */
class SyncNowAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        at.dimes.dmoffice.sync.SyncWorker.now(context)
    }
}

@Composable
private fun HeaderIcon(res: Int, onClick: androidx.glance.action.Action) {
    Image(
        ImageProvider(res), contentDescription = null,
        modifier = GlanceModifier.size(34.dp).padding(6.dp).clickable(onClick),
        colorFilter = ColorFilter.tint(GlanceTheme.colors.onBackground)
    )
}

/**
 * Rahmen mit Kopfzeile wie bei Business Calendar: links der heutige Tag gross mit Wochentag
 * und Monat, rechts neuer Termin, neue Aufgabe, Sync und Einstellungen. Im Aufgaben-Widget
 * ([tasksOnly]) legt das Plus eine Aufgabe an, einen Termin gibt es dort nicht - in [taskList],
 * der ersten im Widget gewaehlten Liste (Einkaufsliste, Checkliste ...).
 * Mit [listBar] (Einkaufsliste) statt des Datums eine Titelleiste in der Listenfarbe wie bei AnyList:
 * Zahnrad, Name der Liste, Plus.
 */
@Composable
internal fun WidgetFrame(title: String, cfg: WidgetConfig, titleIntent: Intent, configIntent: Intent, tasksOnly: Boolean = false, taskList: String? = null,
                         listBar: Pair<String, Int?>? = null, undo: Boolean = false, content: @Composable () -> Unit) {
    val context = androidx.glance.LocalContext.current
    if (listBar != null) {
        Column(modifier = GlanceModifier.fillMaxSize().appWidgetBackground().background(background(cfg.opacity)).cornerRadius(16.dp)) {
            if (cfg.showHeader) {
                val s = cfg.textSize.scale
                val white = ColorProvider(Color.White)
                Row(modifier = GlanceModifier.fillMaxWidth().background(colorOf(listBar.second)).padding(horizontal = 6.dp, vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically) {
                    Image(ImageProvider(R.drawable.ic_settings), contentDescription = null,
                        modifier = GlanceModifier.size(38.dp).padding(8.dp).clickable(actionStartActivity(configIntent)), colorFilter = ColorFilter.tint(white))
                    Text(listBar.first, maxLines = 1, modifier = GlanceModifier.defaultWeight().padding(start = 4.dp).clickable(actionStartActivity(titleIntent)),
                        style = TextStyle(fontSize = (17 * s).sp, fontWeight = FontWeight.Medium, color = white))
                    if (undo) Image(ImageProvider(R.drawable.ic_undo), contentDescription = context.getString(R.string.undo),
                        modifier = GlanceModifier.size(38.dp).padding(8.dp).clickable(actionRunCallback<UndoDoneAction>()), colorFilter = ColorFilter.tint(white))
                    Image(ImageProvider(R.drawable.ic_add), contentDescription = null,
                        modifier = GlanceModifier.size(38.dp).padding(8.dp).clickable(actionStartActivity(MainActivity.openIntent(context, null, null, "task", taskList))),
                        colorFilter = ColorFilter.tint(white))
                }
            }
            // Trennlinien ueber die ganze Breite wie bei AnyList; der Text rueckt in der Zeile ein
            Column(modifier = GlanceModifier.fillMaxSize()) { content() }
        }
        return
    }
    Column(
        modifier = GlanceModifier.fillMaxSize().appWidgetBackground().background(background(cfg.opacity)).cornerRadius(16.dp).padding(horizontal = 10.dp, vertical = 8.dp)
    ) {
        if (cfg.showHeader) {
            val today = LocalDate.now()
            val s = cfg.textSize.scale
            Row(modifier = GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Row(modifier = GlanceModifier.defaultWeight().clickable(actionStartActivity(titleIntent)), verticalAlignment = Alignment.CenterVertically) {
                    Text(today.dayOfMonth.toString(), style = TextStyle(fontWeight = FontWeight.Bold, fontSize = (30 * s).sp, color = GlanceTheme.colors.onBackground))
                    Spacer(GlanceModifier.width(6.dp))
                    Column {
                        Text(today.dayOfWeek.getDisplayName(java.time.format.TextStyle.FULL, java.util.Locale.getDefault()), maxLines = 1,
                            style = TextStyle(fontSize = (12 * s).sp, color = GlanceTheme.colors.onBackground))
                        Text(today.month.getDisplayName(java.time.format.TextStyle.FULL, java.util.Locale.getDefault()), maxLines = 1,
                            style = TextStyle(fontSize = (14 * s).sp, fontWeight = FontWeight.Medium, color = GlanceTheme.colors.onBackground))
                    }
                }
                if (tasksOnly) {
                    HeaderIcon(R.drawable.ic_add, actionStartActivity(MainActivity.openIntent(context, null, null, "task", taskList)))
                } else {
                    HeaderIcon(R.drawable.ic_add, actionStartActivity(MainActivity.openIntent(context, null, null, "event")))
                    HeaderIcon(R.drawable.ic_add_task, actionStartActivity(MainActivity.openIntent(context, null, null, "task")))
                }
                HeaderIcon(R.drawable.ic_refresh, actionRunCallback<SyncNowAction>())
                HeaderIcon(R.drawable.ic_settings, actionStartActivity(configIntent))
            }
            Spacer(GlanceModifier.height(4.dp))
        }
        content()
    }
}

@Composable
internal fun Empty(text: String, cfg: WidgetConfig, fill: Boolean = true) {
    Box(modifier = if (fill) GlanceModifier.fillMaxSize() else GlanceModifier.fillMaxWidth().padding(vertical = 8.dp), contentAlignment = Alignment.Center) {
        Text(text, style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = (13 * cfg.textSize.scale).sp))
    }
}

// ---------------- Daten ----------------

/** Geladene Vorkommen, je Tag verteilt nach der Mehrtages-Regel, plus Kalendernamen. */
internal class WidgetData(val signedIn: Boolean, val byDay: Map<LocalDate, List<Occurrence>>, val calendarNames: Map<String, String>) {
    /** Liste eines Tages; am heutigen Tag ohne bereits vergangene Termine, wenn [hidePast]. */
    fun day(d: LocalDate, today: LocalDate, now: Long, hidePast: Boolean): List<Occurrence> {
        val list = byDay[d] ?: emptyList()
        val shown = if (hidePast && d == today) list.filter { it.allDay || it.end > now } else list
        return shown.sortedWith(compareBy({ !it.allDay }, { it.start }))
    }

    companion object {
        suspend fun load(context: Context, cfg: WidgetConfig, from: LocalDate, to: LocalDate, today: LocalDate): WidgetData {
            val app = App.get(context)
            val signedIn = app.prefs.account.value != null
            if (!signedIn) return WidgetData(false, emptyMap(), emptyMap())
            val zone = ZoneId.systemDefault()
            val fromMs = from.atStartOfDay(zone).toInstant().toEpochMilli()
            val toMs = to.plusDays(1).atStartOfDay(zone).toInstant().toEpochMilli()
            val occ = app.repo.occurrencesNow(fromMs, toMs).filter { o ->
                when (cfg.content) { Content.Both -> true; Content.Events -> !o.isTask; Content.Tasks -> o.isTask } &&
                    (!o.isTask || cfg.showCompleted || !o.item.completed) &&
                    (!o.isTask || !cfg.dueOnly || o.item.due != null)
            }
            val byDay = HashMap<LocalDate, MutableList<Occurrence>>()
            for (o in occ) for (d in MultiDayRule.days(o, zone, cfg.multiDay, from, to, today)) byDay.getOrPut(d) { mutableListOf() }.add(o)
            val names = app.db.calendars().list().associate { it.href to it.name }
            return WidgetData(true, byDay, names)
        }
    }
}

// ---------------- Agenda ----------------

sealed class AgendaRow {
    data class Header(val date: LocalDate) : AgendaRow()
    data class Entry(val o: Occurrence, val day: LocalDate) : AgendaRow()
}

/** Zeilen der Agenda ab [from] ueber [WidgetConfig.days] Tage, hoechstens [WidgetConfig.maxItems] Eintraege. */
internal fun agendaRows(data: WidgetData, cfg: WidgetConfig, from: LocalDate, today: LocalDate, now: Long): List<AgendaRow> {
    val rows = mutableListOf<AgendaRow>()
    var count = 0
    var d = from
    val last = from.plusDays(cfg.days.toLong() - 1)
    while (d <= last && count < cfg.maxItems) {
        val list = data.day(d, today, now, hidePast = from == today)
        if (list.isNotEmpty()) {
            rows.add(AgendaRow.Header(d))
            for (o in list) { rows.add(AgendaRow.Entry(o, d)); if (++count >= cfg.maxItems) break }
        }
        d = d.plusDays(1)
    }
    return rows
}

class AgendaWidget : GlanceAppWidget() {
    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val appWidgetId = try { GlanceAppWidgetManager(context).getAppWidgetId(id) } catch (_: Exception) { 0 }
        val prefs = WidgetPrefs(context)
        val cfg = prefs.load(appWidgetId)
        val today = LocalDate.now(ZoneId.systemDefault())
        val now = System.currentTimeMillis()
        // Mit Monatsraster beginnt die Liste am gewaehlten Tag, sonst heute
        val state = if (cfg.showMonthGrid) prefs.loadMonth(appWidgetId, today) else MonthState(YearMonth.from(today), today)
        val cells = if (cfg.showMonthGrid) MonthGrid.cells(state.month, WeekFields.of(Locale.getDefault()).firstDayOfWeek) else emptyList()
        val from = state.selected
        val last = from.plusDays(cfg.days.toLong() - 1)
        val data = WidgetData.load(context, cfg, minOf(from, cells.firstOrNull() ?: from), maxOf(last, cells.lastOrNull() ?: last), today)
        val rows = agendaRows(data, cfg, from, today, now)
        provideContent {
            GlanceTheme(colors = widgetColors(context)) {
                WidgetFrame(context.getString(R.string.agenda), cfg, MainActivity.openIntent(context, null, "agenda"), Widgets.configIntent(context, appWidgetId, WidgetConfigActivity.KIND_AGENDA)) {
                    if (cfg.showMonthGrid && data.signedIn) {
                        MonthGridView(cfg, state, cells, data, today, WidgetConfigActivity.KIND_AGENDA)
                        Spacer(GlanceModifier.height(6.dp))
                    }
                    when {
                        !data.signedIn -> Empty(context.getString(R.string.widget_not_set_up), cfg)
                        rows.isEmpty() -> Empty(context.getString(R.string.widget_empty), cfg)
                        else -> AgendaList(context, rows, cfg, data)
                    }
                }
            }
        }
    }
}

@Composable
internal fun AgendaList(context: Context, rows: List<AgendaRow>, cfg: WidgetConfig, data: WidgetData) {
    LazyColumn(modifier = GlanceModifier.fillMaxSize()) {
        items(rows) { row ->
            when (row) {
                is AgendaRow.Header -> DayHeaderText(context, row.date, cfg)
                is AgendaRow.Entry -> AgendaLine(context, row.o, row.day, cfg, data.calendarNames[row.o.item.calendarHref])
            }
        }
    }
}

/** Tageskopf: "Heute  KW 43" bzw. "21. Okt.  MITTWOCH, KW 43". */
@Composable
internal fun DayHeaderText(context: Context, date: LocalDate, cfg: WidgetConfig) {
    val s = cfg.textSize.scale
    val today = LocalDate.now()
    val loc = java.util.Locale.getDefault()
    val relative = cfg.dayHeader == DayHeader.Relative && (date == today || date == today.plusDays(1))
    val main = if (relative) context.getString(if (date == today) R.string.today else R.string.tomorrow)
        else java.time.format.DateTimeFormatter.ofPattern("d. MMM", loc).format(date)
    val kw = context.getString(R.string.week_number, date.get(java.time.temporal.WeekFields.ISO.weekOfWeekBasedYear()))
    val sub = (if (relative) "" else date.dayOfWeek.getDisplayName(java.time.format.TextStyle.FULL, loc).uppercase() + ",  ") + kw
    Row(modifier = GlanceModifier.fillMaxWidth().padding(top = 7.dp, bottom = 1.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(main, style = TextStyle(fontWeight = FontWeight.Bold, fontSize = (15 * s).sp, color = if (date == today) GlanceTheme.colors.primary else GlanceTheme.colors.onBackground))
        Spacer(GlanceModifier.width(8.dp))
        Text(sub, maxLines = 1, style = TextStyle(fontSize = (10 * s).sp, color = GlanceTheme.colors.onSurfaceVariant))
    }
}

/** Farbbalken, Punkt oder nichts vor dem Eintrag. */
private val SUP = "\u2070\u00b9\u00b2\u00b3\u2074\u2075\u2076\u2077\u2078\u2079"
private fun sup(n: Int): String = "%02d".format(n).map { SUP[it - '0'] }.joinToString("")

/** Farbiger Kasten mit der Uhrzeit (Minuten hochgestellt) bzw. 24ᴴ fuer ganztaegig. */
@Composable
private fun Badge(context: Context, o: Occurrence, day: LocalDate, cfg: WidgetConfig) {
    val zone = ZoneId.systemDefault()
    val s = cfg.textSize.scale
    val argb = o.color ?: at.dimes.dmoffice.ui.DEFAULT_ACCENT.toInt()
    val lum = (0.299 * ((argb shr 16) and 0xFF) + 0.587 * ((argb shr 8) and 0xFF) + 0.114 * (argb and 0xFF)) / 255
    val fg = ColorProvider(if (lum > 0.6) Color.Black else Color.White)
    val text = if (o.allDay || o.startDate(zone) != day) "24\u1d34" else {
        val t = java.time.Instant.ofEpochMilli(o.start).atZone(zone).toLocalTime()
        val h = if (android.text.format.DateFormat.is24HourFormat(context)) t.hour else ((t.hour + 11) % 12 + 1)
        "%02d".format(h) + sup(t.minute)
    }
    Box(modifier = GlanceModifier.size((38 * s).dp).cornerRadius(4.dp).background(colorOf(o.color)), contentAlignment = Alignment.Center) {
        if (o.isTask) Image(ImageProvider(R.drawable.ic_add_task), contentDescription = null, modifier = GlanceModifier.size((20 * s).dp), colorFilter = ColorFilter.tint(fg))
        else Text(text, maxLines = 1, style = TextStyle(fontSize = (15 * s).sp, fontWeight = FontWeight.Medium, color = fg))
    }
    Spacer(GlanceModifier.width(10.dp))
}

@Composable
private fun Mark(cfg: WidgetConfig, color: Int?, height: Float) {
    when (cfg.colorMark) {
        ColorMark.Badge -> {}
        ColorMark.Bar -> { Box(modifier = GlanceModifier.width(4.dp).height(height.dp).cornerRadius(2.dp).background(colorOf(color))) {}; Spacer(GlanceModifier.width(8.dp)) }
        ColorMark.Dot -> { Box(modifier = GlanceModifier.size(9.dp).cornerRadius(5.dp).background(colorOf(color))) {}; Spacer(GlanceModifier.width(8.dp)) }
        ColorMark.None -> {}
    }
}

@Composable
internal fun AgendaLine(context: Context, o: Occurrence, day: LocalDate, cfg: WidgetConfig, calendarName: String?) {
    val zone = ZoneId.systemDefault()
    val time = when {
        o.allDay -> context.getString(R.string.all_day)
        o.startDate(zone) != day && o.endDate(zone) != day -> context.getString(R.string.all_day)
        o.startDate(zone) != day -> context.getString(R.string.until, Fmt.time(context, o.end))
        else -> Fmt.time(context, o.start)
    }
    val s = cfg.textSize.scale
    val title = o.item.summary.ifBlank { "–" }
    val titleStyle = TextStyle(fontSize = (14 * s).sp, color = GlanceTheme.colors.onBackground, fontWeight = FontWeight.Bold)
    val subStyle = TextStyle(fontSize = (11.5f * s).sp, color = GlanceTheme.colors.onSurfaceVariant)
    val badge = cfg.colorMark == ColorMark.Badge
    val range = when {
        o.isTask -> context.getString(R.string.due) + ": " + (if (o.allDay) context.getString(R.string.all_day).lowercase() else Fmt.time(context, o.start))
        o.allDay && o.endDate(zone) > o.startDate(zone) -> Fmt.shortDate(o.startDate(zone)) + " – " + Fmt.shortDate(o.endDate(zone))
        o.allDay -> context.getString(R.string.all_day)
        else -> Fmt.timeRange(context, o.start, o.end, false)
    }
    val timeText = if (badge) range else if (o.isTask && cfg.rowLayout != RowLayout.Compact) context.getString(R.string.task) + " · " + time else time
    val location = if (cfg.showLocation) o.item.location?.takeIf { it.isNotBlank() } else null
    val lines = when (cfg.rowLayout) {
        RowLayout.Compact -> 1
        RowLayout.Normal -> 2
        RowLayout.Detailed -> 2 + (if (location != null) 1 else 0) + (if (calendarName != null) 1 else 0)
    }
    Row(
        modifier = GlanceModifier.fillMaxWidth().padding(vertical = if (cfg.rowLayout == RowLayout.Compact) 1.dp else 2.dp)
            .clickable(actionStartActivity(MainActivity.openIntent(context, o.item.href))),
        verticalAlignment = Alignment.CenterVertically
    ) {
        if (badge) Badge(context, o, day, cfg) else Mark(cfg, o.color, 14f * lines * s)
        when (cfg.rowLayout) {
            RowLayout.Compact -> {
                if (!badge) Text(time, maxLines = 1, modifier = GlanceModifier.width((52 * s).dp), style = subStyle)
                Text(title, maxLines = 1, modifier = GlanceModifier.defaultWeight(), style = titleStyle)
            }
            RowLayout.Normal -> if (cfg.timePos == TimePos.Left && !badge) {
                Text(timeText, maxLines = 2, modifier = GlanceModifier.width((56 * s).dp), style = subStyle)
                Column(modifier = GlanceModifier.defaultWeight()) {
                    Text(title, maxLines = 1, style = titleStyle)
                    if (location != null) Text(location, maxLines = 1, style = subStyle)
                }
            } else Column(modifier = GlanceModifier.defaultWeight()) {
                Text(title, maxLines = 1, style = titleStyle)
                Text(timeText + (location?.let { " · $it" } ?: ""), maxLines = 1, style = subStyle)
            }
            RowLayout.Detailed -> if (cfg.timePos == TimePos.Left && !badge) {
                Text(timeText, maxLines = 2, modifier = GlanceModifier.width((56 * s).dp), style = subStyle)
                Column(modifier = GlanceModifier.defaultWeight()) {
                    Text(title, maxLines = 2, style = titleStyle)
                    if (location != null) Text(location, maxLines = 1, style = subStyle)
                    if (calendarName != null) Text(calendarName, maxLines = 1, style = subStyle)
                }
            } else Column(modifier = GlanceModifier.defaultWeight()) {
                Text(title, maxLines = 2, style = titleStyle)
                Text(timeText, maxLines = 1, style = subStyle)
                if (location != null) Text(location, maxLines = 1, style = subStyle)
                if (calendarName != null) Text(calendarName, maxLines = 1, style = subStyle)
            }
        }
        if (o.isTask) CheckBox(
            checked = o.item.completed,
            onCheckedChange = actionRunCallback<ToggleTaskAction>(actionParametersOf(hrefKey to o.item.href, doneKey to !o.item.completed)),
        )
    }
}

class AgendaWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = AgendaWidget()
    override fun onDeleted(context: Context, appWidgetIds: IntArray) { super.onDeleted(context, appWidgetIds); WidgetPrefs(context).delete(appWidgetIds) }
}

// ---------------- Monatsraster ----------------

/**
 * Monatsraster: Kopf mit Pfeilen (Monatsname tippt zurueck zu heute), Wochentage, 6 Wochen.
 * Jeder Tag traegt bis zu drei Punkte oder Balken in den Kalenderfarben; Tipp waehlt den Tag.
 */
@Composable
internal fun MonthGridView(cfg: WidgetConfig, state: MonthState, cells: List<LocalDate>, data: WidgetData, today: LocalDate, kind: String) {
    val s = cfg.textSize.scale
    val dim = GlanceTheme.colors.onSurfaceVariant
    Column(modifier = GlanceModifier.fillMaxWidth()) {
        Row(modifier = GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Image(
                ImageProvider(R.drawable.ic_chevron_left), contentDescription = null, colorFilter = ColorFilter.tint(dim),
                modifier = GlanceModifier.size(28.dp).padding(4.dp).clickable(actionRunCallback<MonthNavAction>(actionParametersOf(deltaKey to -1, kindKey to kind)))
            )
            Text(
                Fmt.monthYear(state.month.atDay(1)), maxLines = 1,
                modifier = GlanceModifier.defaultWeight().clickable(actionRunCallback<MonthNavAction>(actionParametersOf(deltaKey to 0, kindKey to kind))),
                style = TextStyle(fontWeight = FontWeight.Bold, fontSize = (13 * s).sp, color = GlanceTheme.colors.onBackground, textAlign = TextAlign.Center)
            )
            Image(
                ImageProvider(R.drawable.ic_chevron_right), contentDescription = null, colorFilter = ColorFilter.tint(dim),
                modifier = GlanceModifier.size(28.dp).padding(4.dp).clickable(actionRunCallback<MonthNavAction>(actionParametersOf(deltaKey to 1, kindKey to kind)))
            )
        }
        Row(modifier = GlanceModifier.fillMaxWidth()) {
            for (d in cells.take(7)) Text(
                Fmt.weekdayNarrow(d), maxLines = 1, modifier = GlanceModifier.defaultWeight(),
                style = TextStyle(fontSize = (10 * s).sp, color = dim, textAlign = TextAlign.Center)
            )
        }
        for (week in cells.chunked(7)) Row(modifier = GlanceModifier.fillMaxWidth()) {
            for (d in week) {
                val inMonth = YearMonth.from(d) == state.month
                val selected = d == state.selected
                val colors = (data.byDay[d] ?: emptyList()).map { it.color }.distinct().take(3)
                var m = GlanceModifier.defaultWeight().padding(1.dp).cornerRadius(6.dp)
                    .clickable(actionRunCallback<SelectDayAction>(actionParametersOf(dateKey to d.toString(), kindKey to kind)))
                if (selected) m = m.background(GlanceTheme.colors.primaryContainer)
                Column(modifier = m, horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(
                        d.dayOfMonth.toString(), maxLines = 1,
                        style = TextStyle(
                            fontSize = (11 * s).sp, textAlign = TextAlign.Center,
                            fontWeight = if (d == today) FontWeight.Bold else FontWeight.Normal,
                            color = when { d == today -> GlanceTheme.colors.primary; selected -> GlanceTheme.colors.onPrimaryContainer; inMonth -> GlanceTheme.colors.onBackground; else -> dim }
                        )
                    )
                    when (cfg.monthMark) {
                        MonthMark.Dots -> Row(modifier = GlanceModifier.height(7.dp), verticalAlignment = Alignment.CenterVertically) {
                            for (c in colors) Box(modifier = GlanceModifier.size(5.dp).cornerRadius(3.dp).background(colorOf(c))) {}
                        }
                        MonthMark.Bars -> Column(modifier = GlanceModifier.fillMaxWidth().height(9.dp).padding(horizontal = 2.dp)) {
                            for (c in colors) Box(modifier = GlanceModifier.fillMaxWidth().height(2.dp).cornerRadius(1.dp).background(colorOf(c)).padding(bottom = 1.dp)) {}
                        }
                    }
                }
            }
        }
    }
}

// ---------------- Aufgaben ----------------

class TasksWidget : GlanceAppWidget() {
    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val app = App.get(context)
        val appWidgetId = try { GlanceAppWidgetManager(context).getAppWidgetId(id) } catch (_: Exception) { 0 }
        val cfg = WidgetPrefs(context).load(appWidgetId, WidgetConfig(content = Content.Tasks, days = 30))
        val signedIn = app.prefs.account.value != null
        val cals = if (signedIn) app.db.calendars().list() else emptyList()
        val visible = cals.filter { it.visible }.map { it.href }.toSet()
        val colors = cals.associate { it.href to it.color }
        val names = cals.associate { it.href to it.name }
        val zone = ZoneId.systemDefault()
        val horizon = LocalDate.now(zone).plusDays(cfg.days.toLong()).atStartOfDay(zone).toInstant().toEpochMilli()
        val all = if (!signedIn) emptyList() else if (cfg.showCompleted) app.db.items().allTasksNow() else app.db.items().openTasksNow()
        // Gewaehlte Listen gelten auch, wenn sie in der App ausgeblendet sind
        // Einkaufs-/Checkliste: nur eine Liste (aeltere Einstellungen konnten mehrere haben)
        val shown = if (cfg.tapToComplete) cfg.lists?.let { sel -> cals.firstOrNull { it.href in sel }?.href }?.let { setOf(it) } ?: visible
            else cfg.lists ?: visible
        // Einkaufsliste: gerade abgehakte bleiben kurz durchgestrichen an ihrem Platz stehen
        val log = if (cfg.tapToComplete && signedIn) WidgetPrefs(context).doneLog(appWidgetId) else emptyList()
        val nowMs = System.currentTimeMillis()
        val striking = log.filter { nowMs - it.second < STRIKE_MS }.mapNotNull { app.db.items().byHref(it.first) }.filter { it.completed && !it.deleted }
        val undo = log.any { nowMs - it.second < 10 * 60_000 }
        val order = compareBy<ItemEntity>({ it.due == null }, { it.due ?: 0L }, { if (it.priority == 0) 10 else it.priority }, { it.summary })
        val tasks = (if (striking.isEmpty()) all else (all.filter { a -> striking.none { it.href == a.href } } + striking).sortedWith(order))
            .filter { it.calendarHref in shown }
            .filter { !cfg.dueOnly || it.due != null }
            .filter { it.due == null || it.due < horizon }
        // E-Mail-Nachverfolgung, wenn ausgewaehlt (nicht im Einkaufslisten-Modus): oben, nach Faelligkeit
        val today = LocalDate.now(zone)
        val last = today.plusDays(cfg.days.toLong())
        val follow = if (!signedIn || cfg.tapToComplete || cfg.lists?.contains(WidgetFollowUps.HREF) != true) emptyList()
            else WidgetFollowUps.load(context)
                .filter { !cfg.dueOnly || it.due != null }
                .filter { f -> f.due == null || try { LocalDate.parse(f.due) <= last } catch (_: Exception) { true } }
                .sortedWith(compareBy({ it.due ?: "9999" }, { it.mail.subject }))
                .take(cfg.maxItems)
        val shownTasks = tasks.take((cfg.maxItems - follow.size).coerceAtLeast(0))
        val mailboxes = if (follow.isEmpty()) emptyMap() else WidgetFollowUps.mailboxes(context)
        val now = System.currentTimeMillis()
        // Neue Aufgabe aus dem Widget: in die erste gewaehlte Liste (Reihenfolge wie in der App)
        val addTo = cfg.lists?.let { sel -> cals.firstOrNull { it.href in sel }?.href }
        // Einkaufsliste: Titelleiste mit Name und Farbe der Liste (bei mehreren: "Aufgaben", Farbe der ersten)
        val bar = if (!cfg.tapToComplete) null else {
            val sel = cals.filter { it.href in shown && it.supportsTasks }
            (sel.singleOrNull()?.name ?: context.getString(R.string.tasks)) to (cals.firstOrNull { it.href == addTo }?.color ?: sel.firstOrNull()?.color)
        }
        provideContent {
            GlanceTheme(colors = widgetColors(context)) {
                WidgetFrame(context.getString(R.string.tasks), cfg, MainActivity.openIntent(context, null, "tasks", list = addTo), Widgets.configIntent(context, appWidgetId, WidgetConfigActivity.KIND_TASKS), tasksOnly = true, taskList = addTo, listBar = bar, undo = undo) {
                    when {
                        !signedIn -> Empty(context.getString(R.string.widget_not_set_up), cfg)
                        shownTasks.isEmpty() && follow.isEmpty() -> Empty(context.getString(R.string.no_tasks), cfg)
                        else -> LazyColumn(modifier = GlanceModifier.fillMaxSize()) {
                            items(follow) { f -> FollowLine(context, f, today, cfg, mailboxes[f.mail.account]) }
                            items(shownTasks) { t -> TaskLine(context, t, colors[t.calendarHref], names[t.calendarHref], now, cfg) }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun TaskLine(context: Context, t: ItemEntity, color: Int?, calendarName: String?, now: Long, cfg: WidgetConfig) {
    val overdue = !t.completed && t.due != null && t.due < now
    val s = cfg.textSize.scale
    val titleStyle = TextStyle(fontSize = (13 * s).sp, color = if (t.completed) GlanceTheme.colors.onSurfaceVariant else GlanceTheme.colors.onBackground,
        textDecoration = if (t.completed && cfg.tapToComplete) TextDecoration.LineThrough else null)
    val due = Fmt.dueLabel(context, t.due, t.allDay)
    val dueStyle = TextStyle(fontSize = (11 * s).sp, color = if (overdue) GlanceTheme.colors.error else GlanceTheme.colors.onSurfaceVariant)
    val subStyle = TextStyle(fontSize = (11 * s).sp, color = GlanceTheme.colors.onSurfaceVariant)
    val title = t.summary.ifBlank { "–" }
    val toggle = actionRunCallback<ToggleTaskAction>(actionParametersOf(hrefKey to t.href, doneKey to !t.completed, tapKey to cfg.tapToComplete))
    // Einkaufsliste: die ganze Zeile hakt ab (wie AnyList), Trennlinie statt Kaestchen; bearbeiten in der App
    if (cfg.tapToComplete) {
        Column(modifier = GlanceModifier.fillMaxWidth()) {
            Row(modifier = GlanceModifier.fillMaxWidth().padding(start = 16.dp, end = 10.dp, top = (8 * s).dp, bottom = (8 * s).dp).clickable(toggle),
                verticalAlignment = Alignment.CenterVertically) {
                Text(title, maxLines = 2, modifier = GlanceModifier.defaultWeight(), style = titleStyle.copy(fontSize = (15 * s).sp))
                if (t.priority in 1..4) Text("!", style = TextStyle(color = GlanceTheme.colors.error, fontWeight = FontWeight.Bold))
            }
            Box(modifier = GlanceModifier.fillMaxWidth().height(1.dp).background(GlanceTheme.colors.outline)) {}
        }
        return
    }
    Row(modifier = GlanceModifier.fillMaxWidth().padding(vertical = 1.dp), verticalAlignment = Alignment.CenterVertically) {
        CheckBox(checked = t.completed, onCheckedChange = toggle)
        Column(modifier = GlanceModifier.defaultWeight().padding(end = 8.dp).clickable(actionStartActivity(MainActivity.openIntent(context, t.href)))) {
            when (cfg.rowLayout) {
                RowLayout.Compact -> Row(modifier = GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(title, maxLines = 1, modifier = GlanceModifier.defaultWeight(), style = titleStyle)
                    if (due != null) Text(due, maxLines = 1, modifier = GlanceModifier.padding(start = 6.dp), style = dueStyle)
                }
                RowLayout.Normal -> {
                    Text(title, maxLines = 1, style = titleStyle)
                    if (due != null) Text(due, maxLines = 1, style = dueStyle)
                }
                RowLayout.Detailed -> {
                    Text(title, maxLines = 2, style = titleStyle)
                    if (due != null) Text(due, maxLines = 1, style = dueStyle)
                    val loc = t.location
                    if (cfg.showLocation && !loc.isNullOrBlank()) Text(loc, maxLines = 1, style = subStyle)
                    if (calendarName != null) Text(calendarName, maxLines = 1, style = subStyle)
                }
            }
        }
        if (t.priority in 1..4) Text("!", style = TextStyle(color = GlanceTheme.colors.error, fontWeight = FontWeight.Bold))
        when (cfg.colorMark) {
            ColorMark.Badge, ColorMark.Bar -> Box(modifier = GlanceModifier.width(4.dp).height((24 * s).dp).cornerRadius(2.dp).background(colorOf(color))) {}
            ColorMark.Dot -> Box(modifier = GlanceModifier.size(9.dp).cornerRadius(5.dp).background(colorOf(color))) {}
            ColorMark.None -> {}
        }
    }
}

/** Gekennzeichnete Mail: Kaestchen = erledigt, Zeile oeffnet die Mail in der App. */
@Composable
private fun FollowLine(context: Context, f: at.dimes.dmoffice.api.FollowUp, today: LocalDate, cfg: WidgetConfig, mailbox: String?) {
    val m = f.mail
    val s = cfg.textSize.scale
    val due = f.due?.let { try { LocalDate.parse(it) } catch (_: Exception) { null } }
    val sub = listOfNotNull(m.from?.text, mailbox, due?.let { Fmt.shortDate(it) }).joinToString(" · ")
    val titleStyle = TextStyle(fontSize = (13 * s).sp, color = GlanceTheme.colors.onBackground)
    val subStyle = TextStyle(fontSize = (11 * s).sp, color = if (due != null && due < today) GlanceTheme.colors.error else GlanceTheme.colors.onSurfaceVariant)
    val done = actionRunCallback<FollowUpDoneAction>(actionParametersOf(accKey to m.account, folderKey to m.folder, uidKey to m.uid))
    Row(modifier = GlanceModifier.fillMaxWidth().padding(vertical = 1.dp), verticalAlignment = Alignment.CenterVertically) {
        CheckBox(checked = false, onCheckedChange = done)
        Column(modifier = GlanceModifier.defaultWeight().padding(end = 8.dp).clickable(actionStartActivity(MainActivity.mailIntent(context, m.account, m.folder, m.uid)))) {
            Text(m.subject.ifBlank { "–" }, maxLines = if (cfg.rowLayout == RowLayout.Detailed) 2 else 1, style = titleStyle)
            if (cfg.rowLayout != RowLayout.Compact && sub.isNotEmpty()) Text(sub, maxLines = 1, style = subStyle)
        }
        when (cfg.colorMark) {
            ColorMark.Badge, ColorMark.Bar -> Box(modifier = GlanceModifier.width(4.dp).height((24 * s).dp).cornerRadius(2.dp).background(colorOf(WidgetFollowUps.COLOR))) {}
            ColorMark.Dot -> Box(modifier = GlanceModifier.size(9.dp).cornerRadius(5.dp).background(colorOf(WidgetFollowUps.COLOR))) {}
            ColorMark.None -> {}
        }
    }
}

class TasksWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = TasksWidget()
    override fun onDeleted(context: Context, appWidgetIds: IntArray) { super.onDeleted(context, appWidgetIds); WidgetPrefs(context).delete(appWidgetIds) }
}

/** Farben der Widgets wie in der App: Akzentfarbe aus den Optionen, Dunkelmodus in Anthrazit. */
internal fun widgetColors(context: Context): androidx.glance.color.ColorProviders {
    val accent = Color(App.get(context).prefs.settings.value.accent.toInt())
    return androidx.glance.material3.ColorProviders(light = at.dimes.dmoffice.ui.lightScheme(accent), dark = at.dimes.dmoffice.ui.darkScheme(accent))
}
