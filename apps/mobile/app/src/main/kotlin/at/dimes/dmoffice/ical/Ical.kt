package at.dimes.dmoffice.ical

import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter

/**
 * Minimaler iCalendar-Leser und -Schreiber (RFC 5545): Zeilen entfalten,
 * NAME;PARAM=WERT:Inhalt zerlegen, Komponenten verschachteln. Unbekannte
 * Eigenschaften bleiben beim Schreiben erhalten (Rundreise).
 */
class Prop(name: String, var value: String) {
    val name: String = name.uppercase()
    val params = mutableListOf<Pair<String, String>>()

    fun param(key: String): String? = params.firstOrNull { it.first.equals(key, true) }?.second

    fun with(key: String, v: String): Prop {
        params.removeAll { it.first.equals(key, true) }
        params.add(key.uppercase() to v)
        return this
    }

    /** Inhalt als Text, entschluesselt (\, \; \n). */
    val text: String get() = Ical.unescape(value)
}

class Component(name: String) {
    val name: String = name.uppercase()
    val props = mutableListOf<Prop>()
    val children = mutableListOf<Component>()

    fun get(n: String): Prop? = props.firstOrNull { it.name == n }
    fun all(n: String): List<Prop> = props.filter { it.name == n }
    fun text(n: String): String? = get(n)?.text
    fun remove(n: String) { props.removeAll { it.name == n } }

    fun set(n: String, raw: String): Prop {
        val p = get(n)
        if (p == null) { val np = Prop(n, raw); props.add(np); return np }
        p.value = raw; p.params.clear(); return p
    }

    /** Textwert setzen; leer entfernt. */
    fun setText(n: String, t: String?) {
        if (t.isNullOrEmpty()) remove(n) else set(n, Ical.escape(t))
    }

    fun of(n: String): List<Component> = children.filter { it.name == n }

    override fun toString(): String = StringBuilder().also { Ical.write(this, it) }.toString()
}

/** Zeitpunkt aus DTSTART/DTEND/DUE: entweder Datum (ganztaegig) oder Instant. */
data class IcalTime(val date: LocalDate?, val instant: Instant?, val zone: ZoneId?) {
    val allDay: Boolean get() = date != null
    fun toInstant(zone: ZoneId): Instant = instant ?: date!!.atStartOfDay(zone).toInstant()
}

object Ical {
    private val DT = DateTimeFormatter.ofPattern("yyyyMMdd'T'HHmmss")
    private val D = DateTimeFormatter.ofPattern("yyyyMMdd")

    // ---------------- Lesen ----------------

    fun parse(text: String): List<Component> {
        val roots = mutableListOf<Component>()
        val stack = ArrayDeque<Component>()
        for (line in unfold(text)) {
            val p = parseLine(line) ?: continue
            when (p.name) {
                "BEGIN" -> {
                    val c = Component(p.value.trim())
                    if (stack.isEmpty()) roots.add(c) else stack.last().children.add(c)
                    stack.addLast(c)
                }
                "END" -> if (stack.isNotEmpty()) stack.removeLast()
                else -> if (stack.isNotEmpty()) stack.last().props.add(p)
            }
        }
        return roots
    }

    private fun unfold(text: String): List<String> {
        val out = mutableListOf<String>()
        val cur = StringBuilder()
        for (raw in text.replace("\r\n", "\n").replace('\r', '\n').split('\n')) {
            if (raw.isNotEmpty() && (raw[0] == ' ' || raw[0] == '\t')) { cur.append(raw, 1, raw.length); continue }
            if (cur.isNotEmpty()) out.add(cur.toString())
            cur.setLength(0); cur.append(raw)
        }
        if (cur.isNotEmpty()) out.add(cur.toString())
        return out
    }

    internal fun parseLine(line: String): Prop? {
        var colon = -1
        var quoted = false
        for (i in line.indices) {
            val ch = line[i]
            if (ch == '"') quoted = !quoted
            else if (ch == ':' && !quoted) { colon = i; break }
        }
        if (colon < 0) return null
        val segs = splitUnquoted(line.substring(0, colon), ';')
        var name = segs[0]
        val dot = name.indexOf('.')
        if (dot > 0) name = name.substring(dot + 1)
        val prop = Prop(name, line.substring(colon + 1))
        for (seg in segs.drop(1)) {
            val eq = seg.indexOf('=')
            if (eq < 0) prop.params.add("TYPE" to seg)
            else prop.params.add(seg.substring(0, eq).uppercase() to seg.substring(eq + 1).trim('"'))
        }
        return prop
    }

    private fun splitUnquoted(s: String, sep: Char): List<String> {
        val out = mutableListOf<String>()
        val sb = StringBuilder()
        var quoted = false
        for (ch in s) {
            if (ch == '"') quoted = !quoted
            if (ch == sep && !quoted) { out.add(sb.toString()); sb.setLength(0) } else sb.append(ch)
        }
        out.add(sb.toString())
        return out
    }

    // ---------------- Schreiben ----------------

    fun write(c: Component, sb: StringBuilder) {
        sb.append("BEGIN:").append(c.name).append("\r\n")
        for (p in c.props) fold(line(p), sb)
        for (ch in c.children) write(ch, sb)
        sb.append("END:").append(c.name).append("\r\n")
    }

    private fun line(p: Prop): String {
        val sb = StringBuilder(p.name)
        for ((k, v) in p.params) {
            sb.append(';').append(k).append('=')
            if (v.any { it == ':' || it == ';' || it == ',' } && !k.equals("TYPE", true)) sb.append('"').append(v).append('"') else sb.append(v)
        }
        return sb.append(':').append(p.value).toString()
    }

    /** Hoechstens 75 Byte je Zeile, ohne ein UTF-8-Zeichen zu teilen. */
    private fun fold(line: String, sb: StringBuilder) {
        var bytes = 0
        var i = 0
        while (i < line.length) {
            val cp = line.codePointAt(i)
            val s = String(Character.toChars(cp))
            val len = s.toByteArray(Charsets.UTF_8).size
            if (bytes + len > 75) { sb.append("\r\n "); bytes = 1 }
            sb.append(s); bytes += len
            i += Character.charCount(cp)
        }
        sb.append("\r\n")
    }

    fun escape(s: String): String = s.replace("\\", "\\\\").replace(";", "\\;").replace(",", "\\,").replace("\r\n", "\\n").replace("\n", "\\n")

    fun unescape(s: String): String {
        if (s.indexOf('\\') < 0) return s
        val sb = StringBuilder(s.length)
        var i = 0
        while (i < s.length) {
            val ch = s[i]
            if (ch == '\\' && i + 1 < s.length) {
                val n = s[i + 1]
                sb.append(if (n == 'n' || n == 'N') '\n' else n)
                i += 2
            } else { sb.append(ch); i++ }
        }
        return sb.toString()
    }

    // ---------------- Zeiten ----------------

    /** Zeitzone aus TZID, auch "/Europe/Berlin" oder Mozilla-Pfade; null wenn unbekannt. */
    fun zone(id: String?): ZoneId? {
        if (id.isNullOrEmpty()) return null
        val parts = id.trimStart('/').split('/')
        val candidates = listOf(id, id.trimStart('/'), parts.takeLast(2).joinToString("/"))
        for (c in candidates) {
            try { return ZoneId.of(c) } catch (_: Exception) {}
        }
        // Windows-Namen wie "W. Europe Standard Time"
        return when {
            id.contains("Europe", true) && id.contains("W.", true) -> ZoneId.of("Europe/Vienna")
            id.contains("Central Europe", true) -> ZoneId.of("Europe/Vienna")
            id.contains("GMT", true) || id.contains("UTC", true) -> ZoneOffset.UTC
            else -> null
        }
    }

    fun time(p: Prop?, fallback: ZoneId): IcalTime? {
        if (p == null) return null
        val v = p.value.trim()
        if (v.isEmpty()) return null
        if (v.length == 8 || p.param("VALUE").equals("DATE", true)) {
            return try { IcalTime(LocalDate.parse(v.take(8), D), null, null) } catch (_: Exception) { null }
        }
        val utc = v.endsWith('Z')
        val core = v.trimEnd('Z').let { if (it.length == 13) it + "00" else it }
        val local = try { LocalDateTime.parse(core, DT) } catch (_: Exception) { return null }
        if (utc) return IcalTime(null, local.toInstant(ZoneOffset.UTC), ZoneOffset.UTC)
        val z = zone(p.param("TZID")) ?: fallback
        return IcalTime(null, ZonedDateTime.of(local, z).toInstant(), z)
    }

    fun utc(i: Instant): String = DT.format(LocalDateTime.ofInstant(i, ZoneOffset.UTC)) + "Z"
    fun date(d: LocalDate): String = D.format(d)
    fun local(i: Instant, z: ZoneId): String = DT.format(LocalDateTime.ofInstant(i, z))
    fun stamp(): String = utc(Instant.now())

    /** DTSTART/DTEND/DUE setzen: ganztaegig als DATE, sonst in der Zone des Handys. */
    fun setTime(c: Component, name: String, t: IcalTime?, zone: ZoneId) {
        if (t == null) { c.remove(name); return }
        if (t.date != null) c.set(name, date(t.date)).with("VALUE", "DATE")
        else {
            val z = t.zone ?: zone
            if (z == ZoneOffset.UTC) c.set(name, utc(t.instant!!))
            else c.set(name, local(t.instant!!, z)).with("TZID", z.id)
        }
    }

    /** Dauer nach RFC 5545 (P1D, PT30M, -PT15M) in Sekunden. */
    fun durationSeconds(s: String?): Long? {
        if (s.isNullOrBlank()) return null
        val m = Regex("""^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$""").find(s.trim()) ?: return null
        val sign = if (m.groupValues[1] == "-") -1 else 1
        val w = m.groupValues[2].toLongOrNull() ?: 0
        val d = m.groupValues[3].toLongOrNull() ?: 0
        val h = m.groupValues[4].toLongOrNull() ?: 0
        val mi = m.groupValues[5].toLongOrNull() ?: 0
        val sec = m.groupValues[6].toLongOrNull() ?: 0
        return sign * (((w * 7 + d) * 24 + h) * 3600 + mi * 60 + sec)
    }

    fun duration(seconds: Long): String {
        val neg = seconds < 0
        var s = Math.abs(seconds)
        val sb = StringBuilder(if (neg) "-P" else "P")
        val days = s / 86400; s %= 86400
        if (days > 0) sb.append(days).append('D')
        if (s > 0 || days == 0L) {
            sb.append('T')
            val h = s / 3600; s %= 3600
            val m = s / 60; s %= 60
            if (h > 0) sb.append(h).append('H')
            if (m > 0 || (h == 0L && s == 0L)) sb.append(m).append('M')
            if (s > 0) sb.append(s).append('S')
        }
        return sb.toString()
    }
}
