package at.dimes.dmoffice.widget

import android.content.Context
import androidx.compose.ui.unit.dp
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetManager
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.lazy.LazyColumn
import androidx.glance.appwidget.lazy.items
import androidx.glance.appwidget.provideContent
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.height
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.MonthGrid
import at.dimes.dmoffice.data.RowLayout
import at.dimes.dmoffice.data.WidgetConfig
import at.dimes.dmoffice.data.WidgetPrefs
import java.time.LocalDate
import java.time.ZoneId
import java.time.temporal.WeekFields
import java.util.Locale

/**
 * Monats-Widget: Raster mit Punkten/Balken je Tag, darunter wahlweise die Liste des
 * gewaehlten Tages. Monat und gewaehlter Tag liegen je appWidgetId in WidgetPrefs.
 */
class MonthWidget : GlanceAppWidget() {
    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val appWidgetId = try { GlanceAppWidgetManager(context).getAppWidgetId(id) } catch (_: Exception) { 0 }
        val prefs = WidgetPrefs(context)
        val cfg = prefs.load(appWidgetId, DEFAULTS)
        val today = LocalDate.now(ZoneId.systemDefault())
        val now = System.currentTimeMillis()
        val state = prefs.loadMonth(appWidgetId, today)
        val cells = MonthGrid.cells(state.month, WeekFields.of(Locale.getDefault()).firstDayOfWeek)
        val data = WidgetData.load(context, cfg, minOf(cells.first(), state.selected), maxOf(cells.last(), state.selected), today)
        val dayList = data.day(state.selected, today, now, hidePast = false).take(cfg.maxItems)
        provideContent {
            GlanceTheme(colors = widgetColors(context)) {
                WidgetFrame(context.getString(R.string.calendar), cfg, MainActivity.openIntent(context, null, "calendar"), Widgets.configIntent(context, appWidgetId, WidgetConfigActivity.KIND_MONTH)) {
                    if (!data.signedIn) Empty(context.getString(R.string.widget_not_set_up), cfg) else {
                        MonthGridView(cfg, state, cells, data, today, WidgetConfigActivity.KIND_MONTH)
                        if (cfg.showDayList) {
                            Spacer(GlanceModifier.height(4.dp))
                            LazyColumn(modifier = GlanceModifier.fillMaxSize()) {
                                item { DayHeaderText(context, state.selected, cfg) }
                                if (dayList.isEmpty()) item { Empty(context.getString(R.string.widget_empty), cfg, fill = false) }
                                items(dayList) { o -> AgendaLine(context, o, state.selected, cfg, data.calendarNames[o.item.calendarHref]) }
                            }
                        }
                    }
                }
            }
        }
    }

    companion object {
        val DEFAULTS = WidgetConfig(days = 1, rowLayout = RowLayout.Compact, showHeader = false, maxItems = 20)
    }
}

class MonthWidgetReceiver : GlanceAppWidgetReceiver() {
    override val glanceAppWidget: GlanceAppWidget = MonthWidget()
    override fun onDeleted(context: Context, appWidgetIds: IntArray) { super.onDeleted(context, appWidgetIds); WidgetPrefs(context).delete(appWidgetIds) }
}
