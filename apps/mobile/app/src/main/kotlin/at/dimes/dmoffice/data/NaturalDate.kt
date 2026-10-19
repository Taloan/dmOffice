package at.dimes.dmoffice.data

import java.time.DayOfWeek
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.temporal.TemporalAdjusters

/**
 * Liest Datum und Uhrzeit aus einem Aufgaben- oder Termintitel, Deutsch und Englisch:
 * "morgen Nachmittag", "naechsten Mittwoch Viertel nach 3", "24.12. 14 Uhr", "in 3 Tagen",
 * "Freitag von 9 bis 11", "tomorrow 3pm", "next friday at noon". Die erkannten Woerter
 * werden aus dem Titel entfernt. Alles ohne Uhrzeit gilt als ganztaegig.
 */
object NaturalDate {
    data class Result(val title: String, val date: LocalDate?, val time: LocalTime?, val endTime: LocalTime?, val endDate: LocalDate? = null) {
        val hasDate: Boolean get() = date != null
        val hasTime: Boolean get() = time != null
        val found: Boolean get() = date != null || time != null
    }

    private val weekdays = mapOf(
        "montag" to DayOfWeek.MONDAY, "dienstag" to DayOfWeek.TUESDAY, "mittwoch" to DayOfWeek.WEDNESDAY, "donnerstag" to DayOfWeek.THURSDAY,
        "freitag" to DayOfWeek.FRIDAY, "samstag" to DayOfWeek.SATURDAY, "sonnabend" to DayOfWeek.SATURDAY, "sonntag" to DayOfWeek.SUNDAY,
        "monday" to DayOfWeek.MONDAY, "tuesday" to DayOfWeek.TUESDAY, "wednesday" to DayOfWeek.WEDNESDAY, "thursday" to DayOfWeek.THURSDAY,
        "friday" to DayOfWeek.FRIDAY, "saturday" to DayOfWeek.SATURDAY, "sunday" to DayOfWeek.SUNDAY,
        "mo." to DayOfWeek.MONDAY, "di." to DayOfWeek.TUESDAY, "mi." to DayOfWeek.WEDNESDAY, "do." to DayOfWeek.THURSDAY,
        "fr." to DayOfWeek.FRIDAY, "sa." to DayOfWeek.SATURDAY, "so." to DayOfWeek.SUNDAY,
        "mon" to DayOfWeek.MONDAY, "tue" to DayOfWeek.TUESDAY, "tues" to DayOfWeek.TUESDAY, "wed" to DayOfWeek.WEDNESDAY, "thu" to DayOfWeek.THURSDAY,
        "thur" to DayOfWeek.THURSDAY, "thurs" to DayOfWeek.THURSDAY, "fri" to DayOfWeek.FRIDAY, "sat" to DayOfWeek.SATURDAY, "sun" to DayOfWeek.SUNDAY,
    )
    private val months = mapOf(
        "januar" to 1, "jänner" to 1, "jaenner" to 1, "jan" to 1, "february" to 2, "februar" to 2, "feber" to 2, "feb" to 2, "märz" to 3, "maerz" to 3, "march" to 3, "mär" to 3, "mar" to 3,
        "april" to 4, "apr" to 4, "mai" to 5, "may" to 5, "juni" to 6, "june" to 6, "jun" to 6, "juli" to 7, "july" to 7, "jul" to 7,
        "august" to 8, "aug" to 8, "september" to 9, "sep" to 9, "sept" to 9, "oktober" to 10, "october" to 10, "okt" to 10, "oct" to 10,
        "november" to 11, "nov" to 11, "dezember" to 12, "december" to 12, "dez" to 12, "dec" to 12, "january" to 1,
    )
    // Tageszeiten: Uhrzeit und ob eher vormittags (fuer "um 3")
    private val dayParts = mapOf(
        "in der früh" to (8 to true), "früh" to (8 to true), "frueh" to (8 to true), "morgens" to (8 to true), "vormittags" to (10 to true), "vormittag" to (10 to true),
        "mittags" to (12 to false), "mittag" to (12 to false), "nachmittags" to (14 to false), "nachmittag" to (14 to false),
        "abends" to (19 to false), "abend" to (19 to false), "nachts" to (22 to false), "nacht" to (22 to false),
        "morning" to (9 to true), "noon" to (12 to false), "midday" to (12 to false), "afternoon" to (14 to false),
        "evening" to (19 to false), "tonight" to (21 to false), "night" to (21 to false),
    )

    private const val W = """(?<![\p{L}\d])"""   // Wortanfang
    private const val E = """(?![\p{L}\d])"""    // Wortende
    private val DAYPART = Regex(W + """(?:am\s+|in\s+the\s+|at\s+)?(in der früh|früh|frueh|morgens|vormittags?|mittags?|nachmittags?|abends?|nachts?|morning|noon|midday|afternoon|evening|tonight|night)""" + E, RegexOption.IGNORE_CASE)
    private val TIME_HM = Regex(W + """(?:um\s+|at\s+|ab\s+|@\s*)?(\d{1,2})[:.](\d{2})\s*(?:uhr|h\b)?\s*(am|pm|a\.m\.|p\.m\.)?""" + E, RegexOption.IGNORE_CASE)
    private val TIME_UHR = Regex(W + """(?:um\s+|ab\s+)?(\d{1,2})\s*uhr(?:\s*(\d{2}))?""" + E, RegexOption.IGNORE_CASE)
    private val TIME_AMPM = Regex(W + """(?:at\s+|@\s*)?(\d{1,2})\s*(am|pm|a\.m\.|p\.m\.)""" + E, RegexOption.IGNORE_CASE)
    private val TIME_BARE = Regex(W + """(?:um|at|@)\s*(\d{1,2})""" + E, RegexOption.IGNORE_CASE)
    private val TIME_WORDS = Regex(W + """(?:um\s+|at\s+)?(halb|viertel\s+nach|viertel\s+vor|dreiviertel|viertel|quarter\s+past|half\s+past|quarter\s+to|half)\s+(\d{1,2})""" + E, RegexOption.IGNORE_CASE)
    // "von 9 bis 11 Uhr", "9-11 Uhr", "20:15-22:30", "from 9 to 11am": nur mit von/from, Minuten oder Einheit, sonst ist "3-4" kein Zeitraum
    private val SPAN = Regex(W + """(von\s+|from\s+)?(\d{1,2})(?:[:.](\d{2}))?\s*(?:uhr\s*)?(?:bis|to|-|–)\s*(\d{1,2})(?:[:.](\d{2}))?\s*(uhr|h|am|pm)?""" + E, RegexOption.IGNORE_CASE)
    private val RANGE = Regex(W + """(?:bis|to|-|–)\s*(\d{1,2})(?:[:.](\d{2}))?\s*(uhr|h\b|am|pm)?""" + E, RegexOption.IGNORE_CASE)
    private val DATE_DMY = Regex(W + """(?:am\s+|on\s+)?(\d{1,2})\.(\d{1,2})\.(?:(\d{4}|\d{2}))?""" + E)
    private val DATE_ISO = Regex(W + """(\d{4})-(\d{2})-(\d{2})""" + E)
    private val DATE_MDY = Regex(W + """(?:on\s+)?(\d{1,2})/(\d{1,2})(?:/(\d{4}|\d{2}))?""" + E)
    private val DATE_DMON = Regex(W + """(?:am\s+|on\s+)?(\d{1,2})\.?\s*(?:of\s+)?(januar|jänner|jaenner|january|jan|februar|feber|february|feb|märz|maerz|march|mär|mar|april|apr|mai|may|juni|june|jun|juli|july|jul|august|aug|september|sept|sep|oktober|october|okt|oct|november|nov|dezember|december|dez|dec)\.?(?:\s+(\d{4}))?""" + E, RegexOption.IGNORE_CASE)
    private val DATE_MOND = Regex(W + """(?:on\s+)?(january|jan|february|feb|march|mar|april|apr|may|june|jun|july|jul|august|aug|september|sept|sep|october|oct|november|nov|december|dec)\.?\s+(\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(\d{4}))?""" + E, RegexOption.IGNORE_CASE)
    private val REL_DAY = Regex(W + """(übermorgen|uebermorgen|day after tomorrow|heute|today|morgen|tomorrow|tmrw|tonight)""" + E, RegexOption.IGNORE_CASE)
    private val REL_IN = Regex(W + """in\s+(\d{1,3}|einer|einem|eine|ein|zwei|drei|vier|fünf|fuenf|sechs|sieben|acht|neun|zehn|a|an|one|two|three|four|five|six|seven|eight|nine|ten)\s+(tagen|tag|wochen|woche|monaten|monat|stunden|stunde|minuten|minute|days|day|weeks|week|months|month|hours|hour|minutes|minute|min)""" + E, RegexOption.IGNORE_CASE)
    private val REL_WEEK = Regex(W + """(nächste|naechste|kommende|next)\s+(woche|week)""" + E, RegexOption.IGNORE_CASE)
    private val WEEKDAY = Regex(W + """(?:(?:am|on|this|diesen|diese|nächsten|naechsten|nächste|naechste|kommenden|kommende|next)\s+)?(montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonnabend|sonntag|monday|tuesday|wednesday|thursday|friday|saturday|sunday|mo\.|di\.|mi\.|do\.|fr\.|sa\.|so\.|mon|tues|tue|wed|thurs|thur|thu|fri|sat|sun)""" + E, RegexOption.IGNORE_CASE)

    private val numberWords = mapOf("einer" to 1, "einem" to 1, "eine" to 1, "ein" to 1, "zwei" to 2, "drei" to 3, "vier" to 4, "fünf" to 5, "fuenf" to 5, "sechs" to 6, "sieben" to 7, "acht" to 8, "neun" to 9, "zehn" to 10,
        "a" to 1, "an" to 1, "one" to 1, "two" to 2, "three" to 3, "four" to 4, "five" to 5, "six" to 6, "seven" to 7, "eight" to 8, "nine" to 9, "ten" to 10)

    fun parse(text: String, now: LocalDateTime = LocalDateTime.now()): Result {
        var s = " $text "
        val spans = mutableListOf<IntRange>()
        var date: LocalDate? = null
        var time: LocalTime? = null
        var endTime: LocalTime? = null
        var morningHint: Boolean? = null
        var dayPartHour: Int? = null
        var explicitAmPm = false
        val today = now.toLocalDate()

        fun take(m: MatchResult) { spans.add(m.range) }
        fun free(m: MatchResult) = spans.none { it.first <= m.range.last && m.range.first <= it.last }

        // ---- Tageszeit (zuerst: entscheidet ueber "um 3") ----
        DAYPART.find(s)?.let { m ->
            val key = m.groupValues[1].lowercase()
            dayParts[key]?.let { (h, am) -> dayPartHour = h; morningHint = am; take(m) }
        }

        // ---- Uhrzeit ----
        fun hour24(h: Int, ampm: String?, hadMinutes: Boolean): Int {
            val ap = ampm?.lowercase()?.replace(".", "")
            if (ap == "pm") { explicitAmPm = true; return if (h == 12) 12 else h + 12 }
            if (ap == "am") { explicitAmPm = true; return if (h == 12) 0 else h }
            // "um 3" / "3 Uhr" / "Viertel nach 3": 1-6 ohne Hinweis auf frueh = nachmittags
            if (h in 1..6 && morningHint != true && !hadMinutes) return h + 12
            if (h in 1..6 && morningHint == false) return h + 12
            return h
        }
        SPAN.find(s)?.takeIf { m -> free(m) && m.groupValues[2].toInt() < 24 && m.groupValues[4].toInt() < 24 &&
                (m.groupValues[1].isNotEmpty() || m.groupValues[3].isNotEmpty() || m.groupValues[5].isNotEmpty() || m.groupValues[6].isNotEmpty()) }?.let { m ->
            val ap = m.groupValues[6].takeIf { it.equals("am", true) || it.equals("pm", true) }
            val h1 = hour24(m.groupValues[2].toInt(), ap, m.groupValues[3].isNotEmpty())
            var h2 = hour24(m.groupValues[4].toInt(), ap, m.groupValues[5].isNotEmpty())
            if (h2 <= h1 && m.groupValues[4].toInt() <= 12 && ap == null) h2 = (m.groupValues[4].toInt() + 12) % 24
            time = LocalTime.of(h1, m.groupValues[3].toIntOrNull() ?: 0)
            endTime = LocalTime.of(h2, m.groupValues[5].toIntOrNull() ?: 0)
            take(m)
        }
        if (time == null) TIME_WORDS.find(s)?.takeIf { free(it) }?.let { m ->
            val word = m.groupValues[1].lowercase().replace(Regex("\\s+"), " ")
            val h = m.groupValues[2].toInt()
            val (hh, mm) = when (word) {
                "halb", "half" -> (h - 1) to 30
                "half past" -> h to 30
                "viertel nach", "quarter past" -> h to 15
                "viertel vor", "quarter to", "dreiviertel" -> (h - 1) to 45
                "viertel" -> (h - 1) to 15
                else -> h to 0
            }
            val base = hour24(((hh % 12) + 12) % 12, null, false).let { if (hh >= 12) hh else it }
            time = LocalTime.of(base % 24, mm); take(m)
        }
        if (time == null) TIME_HM.find(s)?.takeIf { free(it) && it.groupValues[1].toInt() < 24 && it.groupValues[2].toInt() < 60 }?.let { m ->
            // "24.12." ist ein Datum, keine Uhrzeit: nur wenn Minuten 00-59 und (Doppelpunkt oder "uhr"/am/pm oder Stunde > 12)
            val sep = m.value.contains(':')
            val tagged = m.value.contains("uhr", true) || m.groupValues[3].isNotEmpty() || Regex("\\dh$", RegexOption.IGNORE_CASE).containsMatchIn(m.value.trim())
            if (sep || tagged) { time = LocalTime.of(hour24(m.groupValues[1].toInt(), m.groupValues[3].ifEmpty { null }, true), m.groupValues[2].toInt()); take(m) }
        }
        if (time == null) TIME_UHR.find(s)?.takeIf { free(it) && it.groupValues[1].toInt() < 24 }?.let { m ->
            time = LocalTime.of(hour24(m.groupValues[1].toInt(), null, false), m.groupValues[2].toIntOrNull() ?: 0); take(m)
        }
        if (time == null) TIME_AMPM.find(s)?.takeIf { free(it) && it.groupValues[1].toInt() <= 12 }?.let { m ->
            time = LocalTime.of(hour24(m.groupValues[1].toInt(), m.groupValues[2], false), 0); take(m)
        }
        if (time == null) TIME_BARE.find(s)?.takeIf { free(it) && it.groupValues[1].toInt() < 24 }?.let { m ->
            time = LocalTime.of(hour24(m.groupValues[1].toInt(), null, false), 0); take(m)
        }
        if (time == null && dayPartHour != null) time = LocalTime.of(dayPartHour!!, 0)

        // ---- Zeitspanne "bis 16 Uhr" direkt nach einer Uhrzeit ----
        if (time != null) {
            val last = spans.maxByOrNull { it.last }
            if (last != null) {
                val rest = s.substring(last.last + 1)
                RANGE.find(rest)?.takeIf { it.range.first <= 1 }?.let { m ->
                    val h = m.groupValues[1].toInt()
                    if (h < 24) {
                        var hh = hour24(h, m.groupValues[3].takeIf { it.equals("am", true) || it.equals("pm", true) }, m.groupValues[2].isNotEmpty())
                        if (hh <= time!!.hour && h <= 12 && !explicitAmPm) hh = (h + 12) % 24
                        endTime = LocalTime.of(hh, m.groupValues[2].toIntOrNull() ?: 0)
                        spans.add((last.last + 1 + m.range.first)..(last.last + 1 + m.range.last))
                        // "von" davor mit entfernen
                        Regex("""(?:von|from)\s*$""", RegexOption.IGNORE_CASE).find(s.substring(0, spans.minOf { it.first }))?.let { v -> spans.add(v.range) }
                    }
                }
            }
        }

        // ---- Datum ----
        fun year(y: String?, month: Int, day: Int): LocalDate? {
            val yy = y?.toIntOrNull()?.let { if (it < 100) 2000 + it else it }
            return try {
                if (yy != null) LocalDate.of(yy, month, day)
                else {
                    val d = LocalDate.of(today.year, month, day)
                    if (d.isBefore(today.minusDays(1))) d.plusYears(1) else d
                }
            } catch (_: Exception) { null }
        }
        DATE_ISO.find(s)?.takeIf { free(it) }?.let { m -> year(m.groupValues[1], m.groupValues[2].toInt(), m.groupValues[3].toInt())?.let { date = it; take(m) } }
        if (date == null) DATE_DMY.find(s)?.takeIf { free(it) }?.let { m -> year(m.groupValues[3].ifEmpty { null }, m.groupValues[2].toInt(), m.groupValues[1].toInt())?.let { date = it; take(m) } }
        if (date == null) DATE_DMON.find(s)?.takeIf { free(it) }?.let { m ->
            months[m.groupValues[2].lowercase()]?.let { mo -> year(m.groupValues[3].ifEmpty { null }, mo, m.groupValues[1].toInt())?.let { date = it; take(m) } }
        }
        if (date == null) DATE_MOND.find(s)?.takeIf { free(it) }?.let { m ->
            months[m.groupValues[1].lowercase()]?.let { mo -> year(m.groupValues[3].ifEmpty { null }, mo, m.groupValues[2].toInt())?.let { date = it; take(m) } }
        }
        if (date == null) DATE_MDY.find(s)?.takeIf { free(it) }?.let { m -> year(m.groupValues[3].ifEmpty { null }, m.groupValues[1].toInt(), m.groupValues[2].toInt())?.let { date = it; take(m) } }
        if (date == null) REL_DAY.find(s)?.takeIf { free(it) }?.let { m ->
            date = when (m.groupValues[1].lowercase()) {
                "heute", "today", "tonight" -> today
                "morgen", "tomorrow", "tmrw" -> today.plusDays(1)
                else -> today.plusDays(2)
            }
            take(m)
        }
        if (date == null) REL_IN.find(s)?.takeIf { free(it) }?.let { m ->
            val n = m.groupValues[1].toIntOrNull() ?: numberWords[m.groupValues[1].lowercase()] ?: 1
            val unit = m.groupValues[2].lowercase()
            when {
                unit.startsWith("tag") || unit.startsWith("day") -> date = today.plusDays(n.toLong())
                unit.startsWith("woche") || unit.startsWith("week") -> date = today.plusWeeks(n.toLong())
                unit.startsWith("monat") || unit.startsWith("month") -> date = today.plusMonths(n.toLong())
                unit.startsWith("stunde") || unit.startsWith("hour") -> { val t = now.plusHours(n.toLong()); date = t.toLocalDate(); time = t.toLocalTime().withSecond(0).withNano(0) }
                else -> { val t = now.plusMinutes(n.toLong()); date = t.toLocalDate(); time = t.toLocalTime().withSecond(0).withNano(0) }
            }
            take(m)
        }
        if (date == null) REL_WEEK.find(s)?.takeIf { free(it) }?.let { m -> date = today.plusWeeks(1).with(TemporalAdjusters.previousOrSame(DayOfWeek.MONDAY)); take(m) }
        if (date == null) WEEKDAY.find(s)?.takeIf { free(it) }?.let { m ->
            weekdays[m.groupValues[1].lowercase()]?.let { dow ->
                date = today.with(TemporalAdjusters.next(dow)); take(m)
            }
        }

        // Nur Uhrzeit: heute, oder morgen wenn schon vorbei
        if (date == null && time != null) date = if (time!!.isAfter(now.toLocalTime())) today else today.plusDays(1)

        // ---- Titel ohne die erkannten Teile ----
        var title = s
        for (r in spans.sortedByDescending { it.first }) title = title.removeRange(r)
        title = title.replace(Regex("""\s+(am|um|at|on|von|from|bis|to)\s*$""", RegexOption.IGNORE_CASE), " ")
            .replace(Regex("""^\s*(am|um|at|on)\s+""", RegexOption.IGNORE_CASE), " ")
            .replace(Regex("""\s{2,}"""), " ").replace(Regex("""\s+([,.;:])"""), "$1").trim().trimEnd(',', '-', '–', ':').trim()
        if (title.isEmpty()) title = text.trim()
        return Result(title, date, time, endTime)
    }
}
