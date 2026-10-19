package at.dimes.dmoffice.ui

import android.content.Context
import android.text.format.DateFormat
import at.dimes.dmoffice.R
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.time.format.TextStyle
import java.util.Locale

/** Datums- und Zeitformate in der Sprache des Handys. */
object Fmt {
    private val zone: ZoneId get() = ZoneId.systemDefault()

    fun time(context: Context, ms: Long): String {
        val pattern = if (DateFormat.is24HourFormat(context)) "HH:mm" else "h:mm a"
        return DateTimeFormatter.ofPattern(pattern, Locale.getDefault()).format(Instant.ofEpochMilli(ms).atZone(zone))
    }

    fun date(ms: Long): String = DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM).format(Instant.ofEpochMilli(ms).atZone(zone))
    fun date(d: LocalDate): String = DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM).format(d)
    fun shortDate(d: LocalDate): String = DateTimeFormatter.ofPattern("EEE d. MMM", Locale.getDefault()).format(d)
    fun longDate(d: LocalDate): String = DateTimeFormatter.ofLocalizedDate(FormatStyle.FULL).format(d)
    fun monthYear(d: LocalDate): String = DateTimeFormatter.ofPattern("LLLL yyyy", Locale.getDefault()).format(d)
    /** Tageskopf immer als Datum: "Mittwoch, 21. Oktober". */
    fun dayHeader(d: LocalDate): String = DateTimeFormatter.ofPattern("EEEE, d. MMMM", Locale.getDefault()).format(d)
    /** Einbuchstabiger Wochentag fuer das Monatsraster. */
    fun weekdayNarrow(d: LocalDate): String = d.dayOfWeek.getDisplayName(TextStyle.NARROW, Locale.getDefault())
    fun dayName(d: LocalDate): String = d.dayOfWeek.getDisplayName(TextStyle.SHORT, Locale.getDefault())

    fun dateTime(context: Context, ms: Long, allDay: Boolean): String =
        if (allDay) date(ms) else date(ms) + ", " + time(context, ms)

    /** Heute / Morgen / Wochentag, Datum. */
    fun relativeDay(context: Context, d: LocalDate): String {
        val today = LocalDate.now(zone)
        return when (d) {
            today -> context.getString(R.string.today)
            today.plusDays(1) -> context.getString(R.string.tomorrow)
            today.minusDays(1) -> context.getString(R.string.yesterday)
            else -> DateTimeFormatter.ofPattern("EEEE, d. MMMM", Locale.getDefault()).format(d)
        }
    }

    fun timeRange(context: Context, start: Long, end: Long, allDay: Boolean): String {
        if (allDay) {
            val s = Instant.ofEpochMilli(start).atZone(zone).toLocalDate()
            val e = Instant.ofEpochMilli(end).atZone(zone).toLocalDate().minusDays(1)
            return if (e <= s) context.getString(R.string.all_day) else context.getString(R.string.all_day) + ", " + shortDate(s) + " – " + shortDate(e)
        }
        val s = Instant.ofEpochMilli(start).atZone(zone)
        val e = Instant.ofEpochMilli(end).atZone(zone)
        return if (e.toLocalDate() == s.toLocalDate() || end == start) time(context, start) + (if (end > start) " – " + time(context, end) else "")
        else time(context, start) + " – " + shortDate(e.toLocalDate()) + " " + time(context, end)
    }

    fun dueLabel(context: Context, due: Long?, allDay: Boolean): String? {
        if (due == null) return null
        val d = Instant.ofEpochMilli(due).atZone(zone).toLocalDate()
        val today = LocalDate.now(zone)
        val day = when (d) {
            today -> context.getString(R.string.today)
            today.plusDays(1) -> context.getString(R.string.tomorrow)
            today.minusDays(1) -> context.getString(R.string.yesterday)
            else -> shortDate(d)
        }
        return if (allDay) day else day + ", " + time(context, due)
    }

    fun reminderLabel(context: Context, seconds: Long?): String {
        if (seconds == null) return context.getString(R.string.reminder_none)
        if (seconds == 0L) return context.getString(R.string.reminder_at_time)
        val min = (seconds / 60).toInt()
        return when {
            min % 1440 == 0 -> context.resources.getQuantityString(R.plurals.days_before, min / 1440, min / 1440)
            min % 60 == 0 -> context.resources.getQuantityString(R.plurals.hours_before, min / 60, min / 60)
            else -> context.resources.getQuantityString(R.plurals.minutes_before, min, min)
        }
    }

    /** "Mi. 21. Okt., 15:15" bzw. "Mi. 21. Okt." fuer die Erkennung aus dem Titel. */
    fun detected(context: Context, r: at.dimes.dmoffice.data.NaturalDate.Result): String {
        val d = r.date ?: return ""
        val day = relativeDay(context, d).let { if (it.contains(",")) shortDate(d) else it }
        val t = r.time?.let { ", " + java.time.format.DateTimeFormatter.ofPattern(if (DateFormat.is24HourFormat(context)) "HH:mm" else "h:mm a", Locale.getDefault()).format(it) } ?: ""
        val e = r.endTime?.let { " – " + java.time.format.DateTimeFormatter.ofPattern(if (DateFormat.is24HourFormat(context)) "HH:mm" else "h:mm a", Locale.getDefault()).format(it) } ?: ""
        return day + t + e
    }

    fun priorityLabel(context: Context, p: Int): String = when {
        p == 0 -> context.getString(R.string.priority_none)
        p <= 4 -> context.getString(R.string.priority_high)
        p == 5 -> context.getString(R.string.priority_medium)
        else -> context.getString(R.string.priority_low)
    }
}
