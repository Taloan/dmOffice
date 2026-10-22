package at.dimes.dmoffice.files

import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.temporal.ChronoUnit

/**
 * Ansichten der Dateiablage wie im Web (files/FilesView.tsx): Arten, Sortieren, Gruppieren.
 * Reine Funktionen ohne Android, daher in Unit-Tests pruefbar.
 */
object FileViews {
    // Arten - dieselben Namen wie der Server (FileKinds) und das Web (files/kinds.ts)
    const val FOLDER = "folder"; const val IMAGE = "image"; const val PDF = "pdf"; const val DOC = "doc"; const val SHEET = "sheet"
    const val SLIDES = "slides"; const val AUDIO = "audio"; const val VIDEO = "video"; const val ARCHIVE = "archive"; const val TEXT = "text"; const val OTHER = "other"

    /** Arten fuer den Filter der Suche (ohne Ordner und "sonstiges"). */
    val FILTER_KINDS = listOf(IMAGE, VIDEO, AUDIO, PDF, DOC, SHEET, SLIDES, TEXT, ARCHIVE)

    private val EXT = buildMap {
        for (e in listOf("jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "heic", "heif", "tif", "tiff", "ico", "jfif", "avif")) put(e, IMAGE)
        put("pdf", PDF)
        for (e in listOf("doc", "docx", "odt", "rtf", "pages")) put(e, DOC)
        for (e in listOf("xls", "xlsx", "ods", "csv", "numbers")) put(e, SHEET)
        for (e in listOf("ppt", "pptx", "odp", "key")) put(e, SLIDES)
        for (e in listOf("mp3", "m4a", "wav", "ogg", "oga", "opus", "flac", "aac", "wma")) put(e, AUDIO)
        for (e in listOf("mp4", "m4v", "mov", "webm", "mkv", "avi", "wmv", "ogv")) put(e, VIDEO)
        for (e in listOf("zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz")) put(e, ARCHIVE)
        for (e in listOf("txt", "md", "markdown", "log", "json", "xml", "yaml", "yml", "ini", "conf", "cfg", "toml", "cs", "js", "ts", "tsx", "jsx",
            "py", "java", "kt", "c", "h", "cpp", "go", "rs", "sh", "ps1", "bat", "sql", "css", "scss", "html", "htm", "php", "rb", "swift", "ics", "vcf")) put(e, TEXT)
    }

    fun kindOf(name: String, isDir: Boolean, mime: String?): String {
        if (isDir) return FOLDER
        EXT[name.substringAfterLast('.', "").lowercase()]?.let { return it }
        val m = mime ?: ""
        return when {
            m.startsWith("image/") -> IMAGE; m.startsWith("audio/") -> AUDIO; m.startsWith("video/") -> VIDEO
            m.startsWith("text/") -> TEXT; else -> OTHER
        }
    }

    fun kindOf(i: FileItem) = kindOf(i.name, i.isDir, i.mime)

    /** Hat der Server ein Vorschaubild (Bilder, PDF, Videos mit Standbild aus dem Browser)? */
    fun hasThumb(i: FileItem): Boolean = !i.isDir && kindOf(i) in setOf(IMAGE, PDF, VIDEO) && !i.name.endsWith(".svg", ignoreCase = true)

    /** Markdown im Betrachter formatiert zeigen. */
    fun isMarkdown(name: String) = name.substringAfterLast('.', "").lowercase() in setOf("md", "markdown")

    // Sortieren
    const val SORT_NAME = "name"; const val SORT_MODIFIED = "modified"; const val SORT_SIZE = "size"; const val SORT_KIND = "kind"

    /** Ordner immer zuerst, dann nach [by]; Gleichstand nach Namen. */
    fun sort(items: List<FileItem>, by: String, desc: Boolean): List<FileItem> {
        val name = compareBy<FileItem> { it.name.lowercase() }
        val key: Comparator<FileItem> = when (by) {
            SORT_MODIFIED -> compareBy<FileItem> { it.modifiedMs }.then(name)
            SORT_SIZE -> compareBy<FileItem> { it.size }.then(name)
            SORT_KIND -> compareBy<FileItem> { kindOf(it) }.then(name)
            else -> name
        }
        val dirFirst = compareBy<FileItem> { !it.isDir }
        return items.sortedWith(dirFirst.then(if (desc) key.reversed() else key))
    }

    // Gruppieren
    const val GROUP_NONE = "none"; const val GROUP_KIND = "kind"; const val GROUP_DATE = "date"

    /** Datumsgruppen wie im Explorer: 0 Heute, 1 Gestern, 2 Diese Woche, 3 Letzte Woche, 4 Diesen Monat, 5 Dieses Jahr, 6 Aelter. */
    fun dateGroup(ms: Long, today: LocalDate, zone: ZoneId = ZoneId.systemDefault()): Int {
        val d = Instant.ofEpochMilli(ms).atZone(zone).toLocalDate()
        val diff = ChronoUnit.DAYS.between(d, today)
        val weekday = today.dayOfWeek.value - 1   // Montag = 0
        return when {
            diff <= 0 -> 0
            diff == 1L -> 1
            diff <= weekday -> 2
            diff <= weekday + 7 -> 3
            d.year == today.year && d.month == today.month -> 4
            d.year == today.year -> 5
            else -> 6
        }
    }

    /** Eine Gruppe der Anzeige: [key] zum Beschriften (Art bzw. Datumsgruppe), darunter die Eintraege in Anzeigereihenfolge. */
    data class Group(val key: String, val items: List<FileItem>)

    /** Gruppen bilden; innerhalb einer Gruppe bleibt die Sortierung. Ordner (nach Art) bzw. die neueste Gruppe zuerst. */
    fun group(items: List<FileItem>, by: String, today: LocalDate = LocalDate.now(), zone: ZoneId = ZoneId.systemDefault()): List<Group> {
        if (by == GROUP_NONE) return listOf(Group("", items))
        val keyed = items.mapIndexed { n, i ->
            val (order, key) = if (by == GROUP_KIND) { val k = kindOf(i); (if (k == FOLDER) -1 else 0) to k }
                else dateGroup(i.modifiedMs, today, zone).let { it to it.toString() }
            Triple(order to key, n, i)
        }
        return keyed.sortedWith(compareBy({ it.first.first }, { it.first.second }, { it.second }))
            .groupBy { it.first.second }.map { (k, v) -> Group(k, v.map { it.third }) }
    }

    /** Galerie: nach Monat der Aenderung (neueste zuerst), Schluessel "2026-10". */
    fun byMonth(items: List<FileItem>, zone: ZoneId = ZoneId.systemDefault()): List<Group> =
        items.sortedByDescending { it.modifiedMs }.groupBy {
            val d = Instant.ofEpochMilli(it.modifiedMs).atZone(zone).toLocalDate(); "%04d-%02d".format(d.year, d.monthValue)
        }.map { (k, v) -> Group(k, v) }

    /** Ausschnitt der Inhaltssuche: U+E000 … U+E001 umschliessen die Treffer. Liefert Text und Bereiche zum Hervorheben. */
    fun snippet(s: String): Pair<String, List<IntRange>> {
        val out = StringBuilder(); val marks = ArrayList<IntRange>(); var start = -1
        for (c in s) when (c) {
            '' -> start = out.length
            '' -> { if (start >= 0 && out.length > start) marks += start until out.length; start = -1 }
            else -> out.append(c)
        }
        return out.toString() to marks
    }
}
