package at.dimes.dmoffice.ical

import java.time.DayOfWeek
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.temporal.ChronoUnit
import java.time.temporal.TemporalAdjusters

/**
 * RRULE-Ausfaltung (RFC 5545 3.3.10) fuer die gaengigen Faelle: FREQ, INTERVAL,
 * COUNT, UNTIL, BYDAY (auch 2MO / -1FR bei MONTHLY), BYMONTHDAY, BYMONTH, WKST.
 * Reicht, um Serien anzuzeigen; Exoten (BYSETPOS, BYYEARDAY ...) fallen auf die
 * einfache Regel zurueck.
 */
class RRule(text: String) {
    val freq: String
    val interval: Int
    val count: Int?
    val until: String?
    val byDay: List<Pair<Int, DayOfWeek>>   // (Ordnungszahl oder 0, Wochentag)
    val byMonthDay: List<Int>
    val byMonth: List<Int>
    val wkst: DayOfWeek

    init {
        val parts = text.split(';').mapNotNull { val i = it.indexOf('='); if (i < 0) null else it.substring(0, i).uppercase() to it.substring(i + 1) }.toMap()
        freq = parts["FREQ"]?.uppercase() ?: "DAILY"
        interval = parts["INTERVAL"]?.toIntOrNull()?.coerceAtLeast(1) ?: 1
        count = parts["COUNT"]?.toIntOrNull()
        until = parts["UNTIL"]
        byDay = parts["BYDAY"]?.split(',')?.mapNotNull { d ->
            val m = Regex("""^([+-]?\d+)?(MO|TU|WE|TH|FR|SA|SU)$""").find(d.trim().uppercase()) ?: return@mapNotNull null
            (m.groupValues[1].toIntOrNull() ?: 0) to dow(m.groupValues[2])
        } ?: emptyList()
        byMonthDay = parts["BYMONTHDAY"]?.split(',')?.mapNotNull { it.trim().toIntOrNull() } ?: emptyList()
        byMonth = parts["BYMONTH"]?.split(',')?.mapNotNull { it.trim().toIntOrNull() } ?: emptyList()
        wkst = parts["WKST"]?.let { dow(it.uppercase()) } ?: DayOfWeek.MONDAY
    }

    companion object {
        fun dow(s: String): DayOfWeek = when (s) {
            "MO" -> DayOfWeek.MONDAY; "TU" -> DayOfWeek.TUESDAY; "WE" -> DayOfWeek.WEDNESDAY; "TH" -> DayOfWeek.THURSDAY
            "FR" -> DayOfWeek.FRIDAY; "SA" -> DayOfWeek.SATURDAY; else -> DayOfWeek.SUNDAY
        }
        fun abbr(d: DayOfWeek): String = when (d) {
            DayOfWeek.MONDAY -> "MO"; DayOfWeek.TUESDAY -> "TU"; DayOfWeek.WEDNESDAY -> "WE"; DayOfWeek.THURSDAY -> "TH"
            DayOfWeek.FRIDAY -> "FR"; DayOfWeek.SATURDAY -> "SA"; DayOfWeek.SUNDAY -> "SU"
        }
    }

    /**
     * Alle Startzeiten der Serie (als lokale Zeit in [zone]) zwischen [from] und [to].
     * [start] ist der DTSTART der Serie; der zaehlt immer als erstes Vorkommen.
     */
    fun occurrences(start: LocalDateTime, zone: ZoneId, from: Instant, to: Instant, limit: Int = 2000): List<LocalDateTime> {
        val untilLdt = until?.let { u ->
            val p = Prop("UNTIL", u)
            Ical.time(p, zone)?.let { t -> if (t.date != null) t.date.atTime(23, 59, 59) else LocalDateTime.ofInstant(t.instant, zone) }
        }
        val toLdt = LocalDateTime.ofInstant(to, zone)
        val fromLdt = LocalDateTime.ofInstant(from, zone)
        val out = mutableListOf<LocalDateTime>()
        var produced = 0
        var guard = 0
        val time = start.toLocalTime()

        fun emit(d: LocalDate): Boolean {
            val ldt = d.atTime(time)
            if (ldt.isBefore(start)) return true
            if (untilLdt != null && ldt.isAfter(untilLdt)) return false
            produced++
            if (count != null && produced > count) return false
            if (!ldt.isBefore(fromLdt) && ldt.isBefore(toLdt)) out.add(ldt)
            return ldt.isBefore(toLdt) && out.size < limit
        }

        var cursor = start.toLocalDate()
        when (freq) {
            "DAILY" -> {
                while (guard++ < 100000) {
                    if (byMonth.isEmpty() || cursor.monthValue in byMonth)
                        if (byDay.isEmpty() || byDay.any { it.second == cursor.dayOfWeek })
                            if (!emit(cursor)) break
                    cursor = cursor.plusDays(interval.toLong())
                    if (cursor.isAfter(toLdt.toLocalDate())) break
                }
            }
            "WEEKLY" -> {
                val days = if (byDay.isEmpty()) listOf(start.dayOfWeek) else byDay.map { it.second }
                var weekStart = cursor.with(TemporalAdjusters.previousOrSame(wkst))
                outer@ while (guard++ < 20000) {
                    for (i in 0 until 7) {
                        val d = weekStart.plusDays(i.toLong())
                        if (d.dayOfWeek in days && (byMonth.isEmpty() || d.monthValue in byMonth)) if (!emit(d)) break@outer
                    }
                    weekStart = weekStart.plusWeeks(interval.toLong())
                    if (weekStart.isAfter(toLdt.toLocalDate())) break
                }
            }
            "MONTHLY" -> {
                var month = cursor.withDayOfMonth(1)
                outer@ while (guard++ < 20000) {
                    if (byMonth.isEmpty() || month.monthValue in byMonth) {
                        val days = monthDays(month, start.dayOfMonth)
                        for (d in days) if (!emit(d)) break@outer
                    }
                    month = month.plusMonths(interval.toLong())
                    if (month.isAfter(toLdt.toLocalDate())) break
                }
            }
            "YEARLY" -> {
                var year = cursor.withDayOfYear(1)
                outer@ while (guard++ < 5000) {
                    val months = if (byMonth.isEmpty()) listOf(start.monthValue) else byMonth
                    for (m in months) {
                        val month = year.withMonth(m)
                        val days = if (byDay.isEmpty() && byMonthDay.isEmpty()) listOfNotNull(safeDay(month, start.dayOfMonth)) else monthDays(month, start.dayOfMonth)
                        for (d in days) if (!emit(d)) break@outer
                    }
                    year = year.plusYears(interval.toLong())
                    if (year.isAfter(toLdt.toLocalDate())) break
                }
            }
            else -> { emit(cursor) }
        }
        return out
    }

    private fun safeDay(month: LocalDate, day: Int): LocalDate? =
        if (day <= month.lengthOfMonth()) month.withDayOfMonth(day) else null

    /** Tage eines Monats nach BYMONTHDAY / BYDAY, sonst der Starttag. */
    private fun monthDays(month: LocalDate, startDay: Int): List<LocalDate> {
        val out = sortedSetOf<LocalDate>()
        if (byMonthDay.isNotEmpty()) {
            for (md in byMonthDay) {
                val d = if (md > 0) md else month.lengthOfMonth() + md + 1
                safeDay(month, d)?.let { out.add(it) }
            }
        } else if (byDay.isNotEmpty()) {
            for ((ord, dow) in byDay) {
                if (ord == 0) {
                    var d = month.with(TemporalAdjusters.firstInMonth(dow))
                    while (d.month == month.month) { out.add(d); d = d.plusWeeks(1) }
                } else if (ord > 0) {
                    val d = month.with(TemporalAdjusters.dayOfWeekInMonth(ord, dow))
                    if (d.month == month.month) out.add(d)
                } else {
                    val last = month.with(TemporalAdjusters.lastInMonth(dow))
                    val d = last.minusWeeks((-ord - 1).toLong())
                    if (d.month == month.month) out.add(d)
                }
            }
        } else safeDay(month, startDay)?.let { out.add(it) }
        return out.toList()
    }

    /** Kurzform fuer die Anzeige: DAILY/WEEKLY/MONTHLY/YEARLY ohne Zusaetze? */
    val isSimple: Boolean get() = interval == 1 && count == null && until == null && byDay.isEmpty() && byMonthDay.isEmpty() && byMonth.isEmpty()
}

object Recurrence {
    /** Alle Vorkommen (Startzeiten) eines Termins im Bereich, inkl. Einzeltermin. EXDATE beruecksichtigt. */
    fun expand(comp: Component, zone: ZoneId, from: Instant, to: Instant): List<Instant> {
        val start = Ical.time(comp.get("DTSTART"), zone) ?: return emptyList()
        val rrule = comp.get("RRULE")?.value
        val startInstant = start.toInstant(zone)
        val startZone = start.zone ?: zone
        if (rrule.isNullOrBlank()) {
            return if (startInstant.isBefore(to)) listOf(startInstant) else emptyList()
        }
        val rule = try { RRule(rrule) } catch (_: Exception) { return listOf(startInstant) }
        val startLdt = LocalDateTime.ofInstant(startInstant, startZone)
        val exdates = comp.all("EXDATE").flatMap { ex ->
            ex.value.split(',').mapNotNull { v -> Ical.time(Prop("EXDATE", v).also { p -> p.params.addAll(ex.params) }, startZone)?.toInstant(startZone) }
        }.toSet()
        // Bereich grosszuegig: Vorkommen koennen vor "from" beginnen und hineinragen
        val dur = durationOf(comp, zone)
        val list = rule.occurrences(startLdt, startZone, from.minus(dur.coerceAtLeast(0), ChronoUnit.SECONDS).minus(1, ChronoUnit.DAYS), to)
        return list.map { it.atZone(startZone).toInstant() }.filter { it !in exdates && !exdates.contains(stripToDay(it, startZone, start.allDay)) }
    }

    private fun stripToDay(i: Instant, zone: ZoneId, allDay: Boolean): Instant =
        if (allDay) LocalDateTime.ofInstant(i, zone).toLocalDate().atStartOfDay(zone).toInstant() else i

    /** Dauer eines Termins in Sekunden (DTEND, DURATION oder Vorgabe). */
    fun durationOf(comp: Component, zone: ZoneId): Long {
        val start = Ical.time(comp.get("DTSTART"), zone) ?: return 0
        val end = Ical.time(comp.get("DTEND"), zone)
        if (end != null) return java.time.Duration.between(start.toInstant(zone), end.toInstant(zone)).seconds
        Ical.durationSeconds(comp.get("DURATION")?.value)?.let { return it }
        return if (start.allDay) 86400 else 0
    }
}
