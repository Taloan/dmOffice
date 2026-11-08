package at.dimes.dmoffice.data

import android.content.Context
import at.dimes.dmoffice.App
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.util.concurrent.ConcurrentHashMap

/**
 * Gezeigte Kalender (Version 2): nur in dmOffice. CalDAV liefert sie nicht, darum holt die App ihre Termine ueber
 * die API und haelt sie nur im Speicher - nichts davon kommt in den Android-Kalender oder in die eigene Datenbank.
 */
object ShownCalendars {
    /** Kennung gezeigter Termine in der Oberflaeche: "shown:" + Termin-Id des Servers (mit Wiederholung). */
    const val PREFIX = "shown:"

    fun isShown(href: String) = href.startsWith(PREFIX)

    @Serializable
    private data class Coll(val id: Long, val name: String = "", val color: String? = null, val owner: String? = null,
                            val showOnly: Boolean = false, val components: List<String> = emptyList())

    @Serializable
    private data class Ev(val id: String, val calendar: Long, val uid: String? = null, val title: String = "", val location: String? = null,
                          val description: String? = null, val start: String, val end: String, val allDay: Boolean = false, val recurring: Boolean = false)

    /** Ein gezeigter Termin zum Ansehen: Kalendername und wem er gehoert. */
    data class Shown(val item: ItemEntity, val calendar: String, val owner: String?, val color: Int?)

    private val known = ConcurrentHashMap<String, Shown>()
    private val ranges = ConcurrentHashMap<Pair<Long, Long>, List<Occurrence>>()

    /** Zuletzt geholter Stand eines Termins (zum Oeffnen). */
    fun get(href: String): Shown? = known[href]

    /** Vorkommen im Bereich: sofort das Bekannte, dann vom Server, danach alle fuenf Minuten frisch. */
    fun occurrences(context: Context, from: Long, to: Long): Flow<List<Occurrence>> = flow {
        emit(ranges[from to to] ?: emptyList())
        while (true) {
            try { emit(fetch(context, from, to)) } catch (e: Exception) { android.util.Log.w("ShownCalendars", "Gezeigte Kalender: " + e.message) }
            delay(5 * 60_000L)
        }
    }

    private suspend fun fetch(context: Context, from: Long, to: Long): List<Occurrence> = withContext(Dispatchers.IO) {
        val api = App.get(context).api ?: return@withContext emptyList()
        val colls: List<Coll> = api.get(api.url("/api/pim/collections/calendars"))
        val shown = colls.filter { it.showOnly && "VEVENT" in it.components }.associateBy { it.id }
        if (shown.isEmpty()) { ranges[from to to] = emptyList(); return@withContext emptyList() }
        val events: List<Ev> = api.get(api.url("/api/pim/events", "from" to Instant.ofEpochMilli(from).toString(),
            "to" to Instant.ofEpochMilli(to).toString(), "calendars" to shown.keys.joinToString(",")))
        val zone = ZoneId.systemDefault()
        val list = events.mapNotNull { e ->
            val c = shown[e.calendar] ?: return@mapNotNull null
            val start = time(e.start, e.allDay, zone) ?: return@mapNotNull null
            val end = time(e.end, e.allDay, zone) ?: start
            val color = c.color?.let { runCatching { android.graphics.Color.parseColor(it) }.getOrNull() }
            val href = PREFIX + e.id
            val item = ItemEntity(href = href, calendarHref = PREFIX + e.calendar, etag = null, uid = e.uid ?: e.id, kind = KIND_EVENT, ics = "",
                summary = e.title, location = e.location, description = e.description, allDay = e.allDay, start = start, end = end, due = null,
                recurring = e.recurring, completed = false, priority = 0, reminderSeconds = null)
            known[href] = Shown(item, c.name, c.owner, color)
            Occurrence(item, start, end, e.allDay, color)
        }
        ranges[from to to] = list
        list
    }

    /** Zeit vom Server: ganztaegig "yyyy-MM-dd" (Mitternacht hier), sonst ISO in UTC. */
    private fun time(s: String, allDay: Boolean, zone: ZoneId): Long? = try {
        if (allDay || s.length == 10) LocalDate.parse(s.take(10)).atStartOfDay(zone).toInstant().toEpochMilli()
        else Instant.parse(s).toEpochMilli()
    } catch (_: Exception) { null }
}
