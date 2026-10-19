package at.dimes.dmoffice.data

import android.content.Context
import at.dimes.dmoffice.App
import at.dimes.dmoffice.notify.Reminders
import at.dimes.dmoffice.sync.SyncWorker
import at.dimes.dmoffice.widget.Widgets
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
import java.time.ZoneId

/** Lesen fuer die Oberflaeche und lokale Aenderungen; jede Aenderung stoesst den Sync an. */
class Repo(private val context: Context, private val db: AppDb) {
    private val zone: ZoneId get() = ZoneId.systemDefault()

    val calendars: Flow<List<CalendarEntity>> = db.calendars().all()
    val pendingCount: Flow<Int> = db.items().pendingCount()
    val tasks: Flow<List<ItemEntity>> = db.items().tasks()

    /** Termine und faellige Aufgaben im Bereich, nur sichtbare Kalender, zeitlich sortiert. */
    fun occurrences(from: Long, to: Long): Flow<List<Occurrence>> =
        combine(db.items().eventsInRange(from, to), db.items().dueTasks(to), calendars) { events, due, cals ->
            val visible = cals.filter { it.visible }.map { it.href }.toSet()
            val colors = cals.associate { it.href to it.color }
            val ev = Items.expandEvents(events.filter { it.calendarHref in visible }, colors, zone, from, to)
            val td = Items.taskOccurrences(due.filter { it.calendarHref in visible }, colors, zone, from, to)
            (ev + td).sortedWith(compareBy({ it.start }, { !it.allDay }, { it.item.summary }))
        }

    suspend fun occurrencesNow(from: Long, to: Long): List<Occurrence> {
        val cals = db.calendars().list()
        val visible = cals.filter { it.visible }.map { it.href }.toSet()
        val colors = cals.associate { it.href to it.color }
        val ev = Items.expandEvents(db.items().eventsInRangeNow(from, to).filter { it.calendarHref in visible }, colors, zone, from, to)
        val td = Items.taskOccurrences(db.items().dueTasksNow(to).filter { it.calendarHref in visible }, colors, zone, from, to)
        return (ev + td).sortedWith(compareBy({ it.start }, { !it.allDay }, { it.item.summary }))
    }

    suspend fun item(href: String): ItemEntity? = db.items().byHref(href)
    fun observeItem(href: String): Flow<ItemEntity?> = db.items().observe(href)
    suspend fun calendarList(): List<CalendarEntity> = db.calendars().list()
    suspend fun setCalendarVisible(href: String, v: Boolean) { db.calendars().setVisible(href, v); Widgets.updateAll(context) }

    /**
     * Farbe eines Kalenders bzw. einer Aufgabenliste: auf dem Server (gilt dann auch im Web und fuer andere Geraete)
     * und sofort hier. Ohne Verbindung bleibt die alte Farbe; false heisst: nicht geklappt.
     */
    suspend fun setCalendarColor(href: String, argb: Int): Boolean = kotlinx.coroutines.withContext(Dispatchers.IO) {
        val cal = db.calendars().list().firstOrNull { it.href == href } ?: return@withContext false
        val api = at.dimes.dmoffice.App.get(context).api ?: return@withContext false
        val uri = ListTypes.uriOf(href)
        if (ListTypes.byUri.value[uri] == null) ListTypes.refresh(context)
        val id = ListTypes.byUri.value[uri]?.id ?: return@withContext false
        val hex = String.format("#%06X", argb and 0xFFFFFF)
        try {
            api.putText(api.url("/api/pim/collections/$id"),
                kotlinx.serialization.json.buildJsonObject {
                    put("name", kotlinx.serialization.json.JsonPrimitive(cal.name)); put("color", kotlinx.serialization.json.JsonPrimitive(hex))
                }.toString())
        } catch (_: Exception) { return@withContext false }
        db.calendars().setColor(href, argb or 0xFF000000.toInt())
        changed()
        Widgets.updateAll(context)
        true
    }

    // Nacharbeit nach lokalen Aenderungen im Hintergrund: der Abgleich mit dem Android-Kalender
    // schreibt je nach Datenmenge einige Sekunden und darf die Oberflaeche nicht einfrieren
    private val background = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private fun changed() {
        background.launch {
            // Nur Kalender: lokale Aenderungen betreffen Termine und Aufgaben, nie Kontakte
            if (App.get(context).prefs.settings.value.androidCalendar)
                try { at.dimes.dmoffice.sync.ProviderSync.syncCalendars(context) } catch (_: Exception) { }
            Reminders.reschedule(context)
            Widgets.updateAll(context)
            SyncWorker.now(context)
        }
    }

    suspend fun saveEvent(href: String?, calendarHref: String, input: Items.EventInput) {
        val existing = href?.let { db.items().byHref(it) }
        val uid = existing?.uid ?: Items.newUid()
        val targetHref = existing?.href ?: Items.hrefFor(calendarHref, uid)
        val ics = Items.writeEvent(existing?.ics, uid, input, zone)
        val entity = Items.fromIcs(targetHref, calendarHref, existing?.etag, ics, zone, dirty = true) ?: return
        db.items().upsert(entity)
        changed()
    }

    suspend fun saveTask(href: String?, calendarHref: String, input: Items.TaskInput) {
        val existing = href?.let { db.items().byHref(it) }
        val uid = existing?.uid ?: Items.newUid()
        val targetHref = existing?.href ?: Items.hrefFor(calendarHref, uid)
        val ics = Items.writeTask(existing?.ics, uid, input, zone)
        val entity = Items.fromIcs(targetHref, calendarHref, existing?.etag, ics, zone, dirty = true) ?: return
        db.items().upsert(entity)
        changed()
    }

    /** Schnell-Eingabe: nur Titel, in den ersten Kalender mit Aufgaben. */
    suspend fun quickTask(summary: String, due: at.dimes.dmoffice.ical.IcalTime? = null, calendarHref: String? = null,
                          item: String? = null, section: String? = null, qty: String? = null, unit: String? = null) {
        val cal = calendarHref?.let { h -> db.calendars().list().firstOrNull { it.href == h } }
            ?: db.calendars().list().firstOrNull { it.supportsTasks && it.visible } ?: db.calendars().list().firstOrNull() ?: return
        saveTask(null, cal.href, Items.TaskInput(summary, null, due, 0, false, null, item = item, section = section, qty = qty, unit = unit))
    }

    suspend fun setCompleted(href: String, done: Boolean) {
        val item = db.items().byHref(href) ?: return
        if (item.completed == done) return
        db.items().setCompleted(href, done, Items.toggleCompleted(item.ics, done))
        changed()
    }

    suspend fun delete(href: String) {
        val item = db.items().byHref(href) ?: return
        if (item.etag == null) db.items().delete(href) else db.items().upsert(item.copy(deleted = true, dirty = false))
        changed()
    }
}
