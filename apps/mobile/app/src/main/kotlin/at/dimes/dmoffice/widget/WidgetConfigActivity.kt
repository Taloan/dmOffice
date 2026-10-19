package at.dimes.dmoffice.widget

import android.app.Activity
import android.appwidget.AppWidgetManager
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.glance.appwidget.GlanceAppWidgetManager
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Checkbox
import androidx.compose.material3.RadioButton
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.graphics.Color
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.CalendarEntity
import at.dimes.dmoffice.ui.DEFAULT_ACCENT
import at.dimes.dmoffice.data.ColorMark
import at.dimes.dmoffice.data.Content
import at.dimes.dmoffice.data.DayHeader
import at.dimes.dmoffice.data.MonthMark
import at.dimes.dmoffice.data.MultiDay
import at.dimes.dmoffice.data.RowLayout
import at.dimes.dmoffice.data.TextSize
import at.dimes.dmoffice.data.TimePos
import at.dimes.dmoffice.data.WidgetConfig
import at.dimes.dmoffice.data.WidgetPrefs
import at.dimes.dmoffice.ui.AppTheme
import at.dimes.dmoffice.ui.Picker
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Einstellungen eines Widgets: beim Anlegen (ACTION_APPWIDGET_CONFIGURE) und spaeter
 * ueber die Kopfzeile des Widgets. Welches Widget, steht im Intent (EXTRA_WIDGET_KIND).
 */
class WidgetConfigActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val id = intent.getIntExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, AppWidgetManager.INVALID_APPWIDGET_ID)
        setResult(Activity.RESULT_CANCELED, Intent().putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, id))
        if (id == AppWidgetManager.INVALID_APPWIDGET_ID) { finish(); return }
        val kind = intent.getStringExtra(EXTRA_WIDGET_KIND) ?: kindOf(id)
        val prefs = WidgetPrefs(this)
        val defaults = when (kind) {
            KIND_TASKS -> WidgetConfig(content = Content.Tasks, days = 30)
            KIND_MONTH -> MonthWidget.DEFAULTS
            else -> WidgetConfig()
        }

        setContent {
            // Listen fuer die Auswahl im Aufgaben-Widget
            var taskLists by remember { mutableStateOf<List<CalendarEntity>>(emptyList()) }
            LaunchedEffect(kind) {
                if (kind == KIND_TASKS) taskLists = withContext(Dispatchers.IO) { App.get(this@WidgetConfigActivity).db.calendars().list().filter { it.supportsTasks } }
            }
            AppTheme {
                ConfigScreen(prefs.load(id, defaults), kind, taskLists) { c ->
                    prefs.save(id, c)
                    MainScope().launch {
                        withContext(Dispatchers.IO) {
                            try {
                                val glanceId = GlanceAppWidgetManager(this@WidgetConfigActivity).getGlanceIdBy(id)
                                Widgets.update(this@WidgetConfigActivity, glanceId, kind)
                            } catch (_: Exception) { }
                        }
                        setResult(Activity.RESULT_OK, Intent().putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, id))
                        finish()
                    }
                }
            }
        }
    }

    /** Art des Widgets aus dem Provider des appWidgetId. */
    private fun kindOf(id: Int): String {
        val cls = AppWidgetManager.getInstance(this).getAppWidgetInfo(id)?.provider?.className ?: ""
        return when {
            cls.contains("Tasks") -> KIND_TASKS
            cls.contains("Month") -> KIND_MONTH
            else -> KIND_AGENDA
        }
    }

    companion object {
        const val EXTRA_WIDGET_KIND = "kind"
        const val KIND_AGENDA = "agenda"
        const val KIND_TASKS = "tasks"
        const val KIND_MONTH = "month"
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ConfigScreen(initial: WidgetConfig, kind: String, taskLists: List<CalendarEntity>, onDone: (WidgetConfig) -> Unit) {
    val ctx = LocalContext.current
    val isTasks = kind == WidgetConfigActivity.KIND_TASKS
    val isMonth = kind == WidgetConfigActivity.KIND_MONTH
    var c by remember { mutableStateOf(initial) }
    Scaffold(topBar = { TopAppBar(title = { Text(stringResource(R.string.widget_settings)) }) }) { pad ->
      Column(Modifier.padding(pad).fillMaxSize()) {
        // Beispielzeilen bleiben oben stehen, waehrend man unten einstellt
        if (!isMonth || c.showDayList) WidgetPreview(c, tasksOnly = isTasks)
        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            // Inhalt und Zeitraum
            if (!isTasks) Picker(stringResource(R.string.content), c.content, Content.entries, { ctx.getString(it.label) }, { c = c.copy(content = it) })
            if (!isMonth) Picker(stringResource(R.string.days_ahead), c.days, WidgetConfig.DAY_OPTIONS, { ctx.resources.getQuantityString(R.plurals.n_days, it, it) }, { c = c.copy(days = it) })
            if (!isTasks) Picker(stringResource(R.string.multi_day), c.multiDay, MultiDay.entries, { ctx.getString(it.label) }, { c = c.copy(multiDay = it) })
            Picker(stringResource(R.string.max_items), c.maxItems, listOf(5, 10, 20, 50, 100), { it.toString() }, { c = c.copy(maxItems = it) })
            if (isTasks || c.content != Content.Events) {
                ToggleRow(stringResource(R.string.tasks_with_due_only), c.dueOnly) { c = c.copy(dueOnly = it) }
                ToggleRow(stringResource(R.string.show_completed), c.showCompleted) { c = c.copy(showCompleted = it) }
            }
            // Einkaufs-/Checkliste zeigt genau eine Liste: beim Einschalten die erste gewaehlte (oder die erste ueberhaupt)
            if (isTasks) ToggleRow(stringResource(R.string.tap_to_complete), c.tapToComplete) { on ->
                val one = (c.lists?.let { sel -> taskLists.firstOrNull { it.href in sel } } ?: taskLists.firstOrNull())?.href
                c = if (on) c.copy(tapToComplete = true, lists = one?.let { setOf(it) }) else c.copy(tapToComplete = false)
            }

            // Aufgabenlisten: keine Auswahl = alle sichtbaren
            if (isTasks) {
                SectionTitle(stringResource(R.string.task_lists))
                Text(stringResource(if (c.tapToComplete) R.string.task_list_single_hint else R.string.task_lists_hint),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                // E-Mail-Nachverfolgung wie eine Liste dazunehmen (nicht im Einkaufslisten-Modus)
                if (!c.tapToComplete) {
                    val on = c.lists?.contains(WidgetFollowUps.HREF) == true
                    Row(Modifier.fillMaxWidth().clickable {
                        c = c.copy(lists = (c.lists ?: emptySet()).let { if (on) it - WidgetFollowUps.HREF else it + WidgetFollowUps.HREF }.ifEmpty { null })
                    }, verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(checked = on, onCheckedChange = null)
                        Box(Modifier.padding(horizontal = 8.dp).size(12.dp).background(Color(WidgetFollowUps.COLOR), CircleShape))
                        Text(stringResource(R.string.mail_followup), Modifier.weight(1f))
                    }
                }
                for (l in taskLists) {
                    val on = c.lists?.contains(l.href) == true
                    Row(Modifier.fillMaxWidth().clickable {
                        // Einkaufs-/Checkliste: genau eine; normale Aufgaben: beliebig gemischt
                        c = if (c.tapToComplete) c.copy(lists = setOf(l.href))
                            else c.copy(lists = (c.lists ?: emptySet()).let { if (on) it - l.href else it + l.href }.ifEmpty { null })
                    }, verticalAlignment = Alignment.CenterVertically) {
                        if (c.tapToComplete) RadioButton(selected = on, onClick = null) else Checkbox(checked = on, onCheckedChange = null)
                        Box(Modifier.padding(horizontal = 8.dp).size(12.dp).background(Color(l.color ?: DEFAULT_ACCENT.toInt()), CircleShape))
                        Text(l.name, Modifier.weight(1f))
                    }
                }
            }

            // Monatsraster
            if (!isTasks) {
                SectionTitle(stringResource(R.string.month_grid))
                if (!isMonth) ToggleRow(stringResource(R.string.show_month_grid), c.showMonthGrid) { c = c.copy(showMonthGrid = it) }
                if (isMonth) ToggleRow(stringResource(R.string.show_day_list), c.showDayList) { c = c.copy(showDayList = it) }
                if (isMonth || c.showMonthGrid) Picker(stringResource(R.string.month_mark), c.monthMark, MonthMark.entries, { ctx.getString(it.label) }, { c = c.copy(monthMark = it) })
            }

            // Darstellung der Zeilen
            SectionTitle(stringResource(R.string.appearance))
            Picker(stringResource(R.string.row_layout), c.rowLayout, RowLayout.entries, { ctx.getString(it.label) }, { c = c.copy(rowLayout = it) })
            if (!(isTasks && c.tapToComplete)) Picker(stringResource(R.string.color_mark), c.colorMark, ColorMark.entries, { ctx.getString(it.label) }, { c = c.copy(colorMark = it) })
            if (!isTasks && c.rowLayout != RowLayout.Compact) Picker(stringResource(R.string.time_pos), c.timePos, TimePos.entries, { ctx.getString(it.label) }, { c = c.copy(timePos = it) })
            if (!isTasks) Picker(stringResource(R.string.day_header), c.dayHeader, DayHeader.entries, { ctx.getString(it.label) }, { c = c.copy(dayHeader = it) })
            Picker(stringResource(R.string.text_size), c.textSize, TextSize.entries, { ctx.getString(it.label) }, { c = c.copy(textSize = it) })
            Picker(stringResource(R.string.opacity), c.opacity, WidgetConfig.OPACITY_OPTIONS, { "$it %" }, { c = c.copy(opacity = it) })
            ToggleRow(stringResource(R.string.show_header), c.showHeader) { c = c.copy(showHeader = it) }
            if (!isTasks || c.rowLayout == RowLayout.Detailed) ToggleRow(stringResource(R.string.show_location), c.showLocation) { c = c.copy(showLocation = it) }
            Button(onClick = { onDone(c) }, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.done)) }
        }
      }
    }
}

@Composable
private fun SectionTitle(text: String) {
    Text(text, style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(top = 8.dp))
}

@Composable
private fun ToggleRow(label: String, value: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(label, Modifier.weight(1f))
        Switch(checked = value, onCheckedChange = onChange)
    }
}

@Suppress("unused")
private val keepPlural = R.plurals.n_days
@Suppress("unused")
@Composable private fun keepImport() = pluralStringResource(R.plurals.n_days, 1, 1)
