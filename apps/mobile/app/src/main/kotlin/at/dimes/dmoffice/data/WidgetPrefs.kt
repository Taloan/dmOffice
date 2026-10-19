package at.dimes.dmoffice.data

import android.content.Context
import at.dimes.dmoffice.R
import java.time.DayOfWeek
import java.time.LocalDate
import java.time.YearMonth
import java.time.ZoneId

/** Was ein Widget (oder die Agenda) zeigt. */
enum class Content(val label: Int) { Both(R.string.content_both), Events(R.string.content_events), Tasks(R.string.content_tasks) }

/** Wie mehrtaegige Termine in Listen erscheinen. */
enum class MultiDay(val label: Int) { Every(R.string.multi_day_every), FirstLast(R.string.multi_day_first_last), Current(R.string.multi_day_current) }

enum class TextSize(val label: Int, val scale: Float) { Small(R.string.size_small, 0.85f), Normal(R.string.size_normal, 1f), Large(R.string.size_large, 1.2f) }

/** Zeilenlayout eines Eintrags im Widget. */
enum class RowLayout(val label: Int) { Compact(R.string.layout_compact), Normal(R.string.layout_normal), Detailed(R.string.layout_detailed) }

/** Kalenderfarbe am Eintrag: Balken, Punkt oder gar nicht. */
enum class ColorMark(val label: Int) { Badge(R.string.color_badge), Bar(R.string.color_bar), Dot(R.string.color_dot), None(R.string.color_none) }

/** Zeit links neben dem Titel oder darunter. */
enum class TimePos(val label: Int) { Left(R.string.time_left), Below(R.string.time_below) }

/** Tageskopf: "Heute/Morgen/Mittwoch, 21. Oktober" oder immer als Datum. */
enum class DayHeader(val label: Int) { Relative(R.string.day_header_relative), Date(R.string.day_header_date) }

/** Markierung der Tage im Monatsraster. */
enum class MonthMark(val label: Int) { Dots(R.string.month_mark_dots), Bars(R.string.month_mark_bars) }

data class WidgetConfig(
    val content: Content = Content.Both,
    val days: Int = 14,
    val textSize: TextSize = TextSize.Normal,
    val multiDay: MultiDay = MultiDay.Every,
    val opacity: Int = 100,
    val showHeader: Boolean = true,
    val dueOnly: Boolean = false,
    val showLocation: Boolean = true,
    val showCompleted: Boolean = false,
    val maxItems: Int = 50,
    val rowLayout: RowLayout = RowLayout.Normal,
    val colorMark: ColorMark = ColorMark.Badge,
    val timePos: TimePos = TimePos.Below,
    val dayHeader: DayHeader = DayHeader.Relative,
    /** Agenda-Widget: Monatsraster ueber der Liste. */
    val showMonthGrid: Boolean = false,
    val monthMark: MonthMark = MonthMark.Dots,
    /** Monats-Widget: Liste des gewaehlten Tages unter dem Raster. */
    val showDayList: Boolean = true,
    /** Aufgaben-Widget: nur diese Listen (Kalender-hrefs); null = alle sichtbaren. */
    val lists: Set<String>? = null,
    /** Aufgaben-Widget als Einkaufs-/Checkliste: kein Kaestchen, Tippen auf die Zeile hakt ab. */
    val tapToComplete: Boolean = false,
) {
    companion object {
        val DAY_OPTIONS = listOf(1, 2, 3, 7, 14, 30, 60)
        val OPACITY_OPTIONS = listOf(100, 85, 70, 50, 30, 0)
    }
}

/** Einstellungen je Widget-Instanz (appWidgetId) im Klartext-Speicher "widgets". */
class WidgetPrefs(context: Context) {
    private val sp = context.getSharedPreferences("widgets", Context.MODE_PRIVATE)

    fun load(id: Int, defaults: WidgetConfig = WidgetConfig()): WidgetConfig {
        val k = "w$id."
        if (!sp.contains(k + "days")) return defaults
        return WidgetConfig(
            content = enumOr(sp.getString(k + "content", null), defaults.content),
            days = sp.getInt(k + "days", defaults.days),
            textSize = enumOr(sp.getString(k + "text", null), defaults.textSize),
            multiDay = enumOr(sp.getString(k + "multi", null), defaults.multiDay),
            opacity = sp.getInt(k + "opacity", defaults.opacity),
            showHeader = sp.getBoolean(k + "header", defaults.showHeader),
            dueOnly = sp.getBoolean(k + "dueOnly", defaults.dueOnly),
            showLocation = sp.getBoolean(k + "location", defaults.showLocation),
            showCompleted = sp.getBoolean(k + "completed", defaults.showCompleted),
            maxItems = sp.getInt(k + "max", defaults.maxItems),
            rowLayout = enumOr(sp.getString(k + "layout", null), defaults.rowLayout),
            colorMark = enumOr(sp.getString(k + "mark", null), defaults.colorMark),
            timePos = enumOr(sp.getString(k + "timePos", null), defaults.timePos),
            dayHeader = enumOr(sp.getString(k + "dayHeader", null), defaults.dayHeader),
            showMonthGrid = sp.getBoolean(k + "grid", defaults.showMonthGrid),
            monthMark = enumOr(sp.getString(k + "monthMark", null), defaults.monthMark),
            showDayList = sp.getBoolean(k + "dayList", defaults.showDayList),
            lists = sp.getStringSet(k + "lists", null)?.toSet()?.takeIf { it.isNotEmpty() },
            tapToComplete = sp.getBoolean(k + "tapDone", defaults.tapToComplete),
        )
    }

    fun save(id: Int, c: WidgetConfig) {
        val k = "w$id."
        sp.edit().putString(k + "content", c.content.name).putInt(k + "days", c.days).putString(k + "text", c.textSize.name)
            .putString(k + "multi", c.multiDay.name).putInt(k + "opacity", c.opacity).putBoolean(k + "header", c.showHeader)
            .putBoolean(k + "dueOnly", c.dueOnly).putBoolean(k + "location", c.showLocation).putBoolean(k + "completed", c.showCompleted)
            .putInt(k + "max", c.maxItems)
            .putString(k + "layout", c.rowLayout.name).putString(k + "mark", c.colorMark.name).putString(k + "timePos", c.timePos.name)
            .putString(k + "dayHeader", c.dayHeader.name).putBoolean(k + "grid", c.showMonthGrid).putString(k + "monthMark", c.monthMark.name)
            .putBoolean(k + "dayList", c.showDayList).putBoolean(k + "tapDone", c.tapToComplete)
            .apply { if (c.lists.isNullOrEmpty()) remove(k + "lists") else putStringSet(k + "lists", c.lists) }.apply()
    }

    /** Zustand des Monatsrasters (angezeigter Monat, gewaehlter Tag) je Widget; ohne Eintrag heute. */
    fun loadMonth(id: Int, today: LocalDate): MonthState {
        val k = "w$id."
        val month = try { sp.getString(k + "month", null)?.let { YearMonth.parse(it) } } catch (_: Exception) { null }
        val sel = try { sp.getString(k + "selected", null)?.let { LocalDate.parse(it) } } catch (_: Exception) { null }
        return MonthState(month ?: YearMonth.from(sel ?: today), sel ?: today)
    }

    fun saveMonth(id: Int, s: MonthState) {
        val k = "w$id."
        sp.edit().putString(k + "month", s.month.toString()).putString(k + "selected", s.selected.toString()).apply()
    }

    /** Einkaufslisten-Widget: zuletzt abgehakte Eintraege (href, Zeitpunkt), neueste zuletzt - fuer Durchstreichen und Rueckgaengig. */
    fun doneLog(id: Int): List<Pair<String, Long>> =
        (sp.getString("w$id.done", null) ?: "").lines().mapNotNull { l ->
            val i = l.lastIndexOf('|'); if (i <= 0) null else l.substring(0, i) to (l.substring(i + 1).toLongOrNull() ?: return@mapNotNull null)
        }

    private fun saveDoneLog(id: Int, log: List<Pair<String, Long>>) {
        val now = System.currentTimeMillis()
        // Rueckgaengig reicht zehn Minuten und zwanzig Eintraege zurueck
        val keep = log.filter { now - it.second < 10 * 60_000 }.takeLast(20)
        sp.edit().putString("w$id.done", keep.joinToString("\n") { "${it.first}|${it.second}" }).apply()
    }

    fun pushDone(id: Int, href: String) = saveDoneLog(id, doneLog(id).filter { it.first != href } + (href to System.currentTimeMillis()))
    fun removeDone(id: Int, href: String) = saveDoneLog(id, doneLog(id).filter { it.first != href })
    fun popDone(id: Int): String? = doneLog(id).lastOrNull()?.also { removeDone(id, it.first) }?.first

    fun delete(ids: IntArray) {
        val e = sp.edit()
        for (id in ids) for (key in sp.all.keys) if (key.startsWith("w$id.")) e.remove(key)
        e.apply()
    }

    private inline fun <reified T : Enum<T>> enumOr(name: String?, def: T): T = try { if (name == null) def else enumValueOf<T>(name) } catch (_: Exception) { def }
}

object MultiDayRule {
    /**
     * Tage, an denen ein Vorkommen in einer Tagesliste erscheint. [from]..[to] ist der sichtbare
     * Bereich; [today] entscheidet bei "nur am laufenden Tag".
     */
    fun days(o: Occurrence, zone: ZoneId, mode: MultiDay, from: LocalDate, to: LocalDate, today: LocalDate): List<LocalDate> {
        val start = o.startDate(zone)
        val end = maxOf(o.endDate(zone), start)
        if (end < from || start > to) return emptyList()
        if (start == end) return listOf(start)
        return when (mode) {
            MultiDay.Every -> {
                val out = mutableListOf<LocalDate>()
                var d = maxOf(start, from)
                var n = 0
                while (d <= minOf(end, to) && n++ < 400) { out.add(d); d = d.plusDays(1) }
                out
            }
            MultiDay.FirstLast -> listOf(start, end).filter { it in from..to }
            MultiDay.Current -> {
                val d = if (today in start..end) today else start
                if (d in from..to) listOf(d) else emptyList()
            }
        }
    }
}

data class MonthState(val month: YearMonth, val selected: LocalDate)

object MonthGrid {
    /** 42 Tage (6 Wochen) ab dem ersten Wochentag der Woche, die den Monatsersten enthaelt. */
    fun cells(month: YearMonth, firstDay: DayOfWeek): List<LocalDate> {
        val first = month.atDay(1)
        val shift = ((first.dayOfWeek.value - firstDay.value) + 7) % 7
        val start = first.minusDays(shift.toLong())
        return (0 until 42).map { start.plusDays(it.toLong()) }
    }
}
