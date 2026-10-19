package at.dimes.dmoffice.data

import at.dimes.dmoffice.ical.Component
import at.dimes.dmoffice.ical.Ical
import at.dimes.dmoffice.ical.IcalTime
import at.dimes.dmoffice.ical.Recurrence
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.util.UUID

/** Ein konkretes Vorkommen eines Termins (bei Serien eines von vielen) oder eine faellige Aufgabe im Kalender. */
data class Occurrence(
    val item: ItemEntity,
    val start: Long,
    val end: Long,
    val allDay: Boolean,
    val color: Int?,
) {
    val isTask: Boolean get() = item.kind == KIND_TASK
    fun startDate(zone: ZoneId): LocalDate = Instant.ofEpochMilli(start).atZone(zone).toLocalDate()
    /** Letzter Tag (ganztaegige Termine enden exklusiv um Mitternacht). */
    fun endDate(zone: ZoneId): LocalDate {
        val e = Instant.ofEpochMilli(end).atZone(zone)
        return if (allDay || (e.toLocalTime().toSecondOfDay() == 0 && end > start)) e.toLocalDate().minusDays(1) else e.toLocalDate()
    }
}

object Items {
    const val PRODID = "-//dimes.at//dmOffice Android//DE"

    /** Hauptkomponente eines Objekts: der VEVENT/VTODO ohne RECURRENCE-ID, sonst der erste. */
    fun master(cal: Component): Component? {
        val comps = cal.children.filter { it.name == "VEVENT" || it.name == "VTODO" }
        return comps.firstOrNull { it.get("RECURRENCE-ID") == null } ?: comps.firstOrNull()
    }

    fun parseCalendar(ics: String): Component? = Ical.parse(ics).firstOrNull { it.name == "VCALENDAR" }

    /** Felder fuer die Datenbank aus dem iCalendar-Text; null wenn kein VEVENT/VTODO drin ist. */
    fun fromIcs(href: String, calendarHref: String, etag: String?, ics: String, zone: ZoneId, dirty: Boolean = false): ItemEntity? {
        val cal = parseCalendar(ics) ?: return null
        val m = master(cal) ?: return null
        val isTask = m.name == "VTODO"
        val start = Ical.time(m.get("DTSTART"), zone)
        val due = Ical.time(m.get("DUE"), zone)
        val allDay = start?.allDay ?: due?.allDay ?: false
        val startMs = start?.toInstant(zone)?.toEpochMilli()
        var endMs: Long? = null
        if (!isTask && start != null) {
            endMs = start.toInstant(zone).plusSeconds(Recurrence.durationOf(m, zone)).toEpochMilli()
            if (endMs <= startMs!!) endMs = if (allDay) startMs + 86400_000 else startMs
        }
        val completedAt = m.get("COMPLETED")
        val completed = isTask && (completedAt != null || m.text("STATUS").equals("COMPLETED", true))
        val reminder = reminderSeconds(m)
        return ItemEntity(
            href = href, calendarHref = calendarHref, etag = etag,
            uid = m.text("UID") ?: href.substringAfterLast('/'),
            kind = if (isTask) KIND_TASK else KIND_EVENT,
            ics = ics,
            summary = m.text("SUMMARY")?.trim().orEmpty(),
            location = m.text("LOCATION")?.takeIf { it.isNotBlank() },
            description = m.text("DESCRIPTION")?.takeIf { it.isNotBlank() },
            allDay = allDay,
            start = startMs,
            end = endMs,
            due = due?.toInstant(zone)?.toEpochMilli(),
            recurring = m.get("RRULE") != null || m.get("RDATE") != null,
            completed = completed,
            priority = m.text("PRIORITY")?.toIntOrNull()?.coerceIn(0, 9) ?: 0,
            reminderSeconds = reminder,
            dirty = dirty,
        )
    }

    /** Erster VALARM mit TRIGGER relativ zum Beginn: Sekunden vorher (positiv), null ohne Alarm. */
    fun reminderSeconds(m: Component): Long? {
        for (alarm in m.of("VALARM")) {
            val trig = alarm.get("TRIGGER") ?: continue
            if (trig.param("VALUE").equals("DATE-TIME", true)) continue
            val secs = Ical.durationSeconds(trig.value) ?: continue
            return -secs
        }
        return null
    }

    fun setReminder(m: Component, secondsBefore: Long?) {
        m.children.removeAll { it.name == "VALARM" }
        if (secondsBefore == null) return
        val alarm = Component("VALARM")
        alarm.set("ACTION", "DISPLAY")
        alarm.set("DESCRIPTION", m.get("SUMMARY")?.value ?: "Reminder")
        alarm.set("TRIGGER", Ical.duration(-secondsBefore))
        m.children.add(alarm)
    }

    /** Alle Vorkommen der Termine (inkl. Serien, RECURRENCE-ID-Ausnahmen) im Bereich. */
    fun expandEvents(items: List<ItemEntity>, colors: Map<String, Int?>, zone: ZoneId, from: Long, to: Long): List<Occurrence> {
        val out = mutableListOf<Occurrence>()
        val fromI = Instant.ofEpochMilli(from)
        val toI = Instant.ofEpochMilli(to)
        for (it in items) {
            val color = colors[it.calendarHref]
            if (!it.recurring) {
                if (it.start != null && it.end != null && it.end > from && it.start < to) out.add(Occurrence(it, it.start, it.end, it.allDay, color))
                continue
            }
            val cal = parseCalendar(it.ics) ?: continue
            val comps = cal.of("VEVENT")
            val m = comps.firstOrNull { c -> c.get("RECURRENCE-ID") == null } ?: continue
            val overrides = comps.filter { c -> c.get("RECURRENCE-ID") != null }
            val overriddenStarts = overrides.mapNotNull { c -> Ical.time(c.get("RECURRENCE-ID"), zone)?.toInstant(zone)?.toEpochMilli() }.toSet()
            val dur = Recurrence.durationOf(m, zone) * 1000
            for (s in Recurrence.expand(m, zone, fromI, toI)) {
                val sm = s.toEpochMilli()
                if (sm in overriddenStarts) continue
                val em = if (dur > 0) sm + dur else if (it.allDay) sm + 86400_000 else sm
                if (em > from && sm < to) out.add(Occurrence(it, sm, em, it.allDay, color))
            }
            for (o in overrides) {
                val st = Ical.time(o.get("DTSTART"), zone) ?: continue
                val sm = st.toInstant(zone).toEpochMilli()
                val em = sm + Recurrence.durationOf(o, zone) * 1000
                if (em > from && sm < to) {
                    val ov = it.copy(summary = o.text("SUMMARY") ?: it.summary, location = o.text("LOCATION") ?: it.location)
                    out.add(Occurrence(ov, sm, if (em > sm) em else sm, st.allDay, color))
                }
            }
        }
        return out.sortedWith(compareBy({ !it.allDay }, { it.start }))
    }

    /** Faellige Aufgaben als Kalendereintraege am Faelligkeitstag. */
    fun taskOccurrences(tasks: List<ItemEntity>, colors: Map<String, Int?>, zone: ZoneId, from: Long, to: Long): List<Occurrence> =
        tasks.filter { it.due != null && it.due >= from && it.due < to }.map {
            val allDay = it.allDay
            Occurrence(it, it.due!!, if (allDay) it.due + 86400_000 else it.due, allDay, colors[it.calendarHref])
        }

    // ---------------- Schreiben ----------------

    fun newCalendar(): Component {
        val cal = Component("VCALENDAR")
        cal.set("VERSION", "2.0")
        cal.set("PRODID", PRODID)
        return cal
    }

    fun newUid(): String = UUID.randomUUID().toString()

    /** Hoechstens ein Objekt je UID: Dateiname aus der UID. */
    fun hrefFor(calendarHref: String, uid: String): String = calendarHref + uid.replace(Regex("[^A-Za-z0-9@._-]"), "_") + ".ics"

    data class EventInput(
        val summary: String, val location: String?, val description: String?,
        val start: IcalTime, val end: IcalTime, val rrule: String?, val reminderSeconds: Long?,
    )

    data class TaskInput(
        val summary: String, val description: String?, val due: IcalTime?, val priority: Int,
        val completed: Boolean, val reminderSeconds: Long?,
        /** Einkaufs-/Checkliste: Artikel, Einheit, Abteilung als X-DM-ITEM, X-DM-UNIT, X-DM-SECTION (wie im Web). */
        val item: String? = null, val unit: String? = null, val section: String? = null, val qty: String? = null,
    )

    /** Termin in ein (bestehendes oder neues) VCALENDAR schreiben; nur bekannte Felder anfassen. */
    fun writeEvent(existing: String?, uid: String, input: EventInput, zone: ZoneId): String {
        val cal = existing?.let { parseCalendar(it) } ?: newCalendar()
        val m = cal.of("VEVENT").firstOrNull { it.get("RECURRENCE-ID") == null } ?: Component("VEVENT").also { cal.children.add(it) }
        if (m.get("UID") == null) m.set("UID", uid)
        if (m.get("CREATED") == null) m.set("CREATED", Ical.stamp())
        m.set("DTSTAMP", Ical.stamp())
        m.set("LAST-MODIFIED", Ical.stamp())
        m.setText("SUMMARY", input.summary)
        m.setText("LOCATION", input.location)
        m.setText("DESCRIPTION", input.description)
        Ical.setTime(m, "DTSTART", input.start, zone)
        m.remove("DURATION")
        Ical.setTime(m, "DTEND", input.end, zone)
        if (input.rrule.isNullOrBlank()) m.remove("RRULE") else m.set("RRULE", input.rrule)
        m.set("SEQUENCE", ((m.text("SEQUENCE")?.toIntOrNull() ?: 0) + 1).toString())
        setReminder(m, input.reminderSeconds)
        return cal.toString()
    }

    fun writeTask(existing: String?, uid: String, input: TaskInput, zone: ZoneId): String {
        val cal = existing?.let { parseCalendar(it) } ?: newCalendar()
        val m = cal.of("VTODO").firstOrNull() ?: Component("VTODO").also { cal.children.add(it) }
        if (m.get("UID") == null) m.set("UID", uid)
        if (m.get("CREATED") == null) m.set("CREATED", Ical.stamp())
        m.set("DTSTAMP", Ical.stamp())
        m.set("LAST-MODIFIED", Ical.stamp())
        m.setText("SUMMARY", input.summary)
        m.setText("DESCRIPTION", input.description)
        Ical.setTime(m, "DUE", input.due, zone)
        // DTSTART nach DUE waere ungueltig
        val ds = Ical.time(m.get("DTSTART"), zone)
        if (input.due != null && ds != null && ds.toInstant(zone).isAfter(input.due.toInstant(zone))) m.remove("DTSTART")
        if (input.priority > 0) m.set("PRIORITY", input.priority.coerceIn(1, 9).toString()) else m.remove("PRIORITY")
        input.item?.let { m.setText("X-DM-ITEM", it) }
        input.unit?.let { m.setText("X-DM-UNIT", it) }
        input.qty?.let { m.setText("X-DM-QTY", it) }
        input.section?.let { m.setText("X-DM-SECTION", it) }
        setCompleted(m, input.completed)
        m.set("SEQUENCE", ((m.text("SEQUENCE")?.toIntOrNull() ?: 0) + 1).toString())
        setReminder(m, input.reminderSeconds)
        return cal.toString()
    }

    /** Erledigt-Zustand wie dmOffice selbst: COMPLETED, STATUS und PERCENT-COMPLETE zusammen. */
    fun setCompleted(m: Component, done: Boolean) {
        if (done) {
            if (m.get("COMPLETED") == null) m.set("COMPLETED", Ical.stamp())
            m.set("STATUS", "COMPLETED")
            m.set("PERCENT-COMPLETE", "100")
        } else {
            m.remove("COMPLETED")
            if (m.text("STATUS").equals("COMPLETED", true)) m.set("STATUS", "NEEDS-ACTION")
            if (m.text("PERCENT-COMPLETE") == "100") m.remove("PERCENT-COMPLETE")
        }
    }

    fun toggleCompleted(ics: String, done: Boolean): String {
        val cal = parseCalendar(ics) ?: return ics
        val m = cal.of("VTODO").firstOrNull() ?: return ics
        setCompleted(m, done)
        m.set("DTSTAMP", Ical.stamp())
        m.set("LAST-MODIFIED", Ical.stamp())
        return cal.toString()
    }

    /** Kopie unter neuer UID (fuer Konflikte). */
    fun copyWithNewUid(ics: String, uid: String, summarySuffix: String): String? {
        val cal = parseCalendar(ics) ?: return null
        for (c in cal.children) if (c.name == "VEVENT" || c.name == "VTODO") {
            c.set("UID", uid)
            c.setText("SUMMARY", (c.text("SUMMARY") ?: "") + summarySuffix)
        }
        return cal.toString()
    }
}
