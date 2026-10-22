package at.dimes.dmoffice.upload

import java.time.Instant
import java.time.ZoneId

/**
 * Reine Regeln des automatischen Hochladens (ohne Android, daher in Unit-Tests pruefbar):
 * Zielpfad mit Unterordnern nach Datum, Dateiarten, Wiederholen mit Wartezeit, gesperrte Ziele.
 */
object UploadLogic {
    // Quellen
    const val SRC_CAMERA = "camera"
    const val SRC_SCREENSHOTS = "screenshots"
    const val SRC_WHATSAPP = "whatsapp"
    const val SRC_MEDIA = "media"   // anderer Medienordner aus dem MediaStore
    const val SRC_TREE = "tree"     // beliebiger Ordner ueber das Storage Access Framework

    /** Ordner (RELATIVE_PATH im MediaStore) der vorgegebenen Quellen; je nach Hersteller und Android-Version verschieden. */
    val PRESET_PATHS = mapOf(
        SRC_CAMERA to listOf("DCIM/Camera/", "DCIM/100ANDRO/", "DCIM/100MEDIA/", "DCIM/OpenCamera/"),
        SRC_SCREENSHOTS to listOf("DCIM/Screenshots/", "Pictures/Screenshots/", "Pictures/Screenshot/"),
        SRC_WHATSAPP to listOf("Android/media/com.whatsapp/WhatsApp/Media/", "WhatsApp/Media/", "Android/media/com.whatsapp.w4b/WhatsApp Business/Media/"),
    )

    // Unterordner nach Datum
    const val SUB_NONE = "none"
    const val SUB_YEAR = "year"
    const val SUB_MONTH = "month"
    const val SUB_DAY = "day"

    // Dateiarten
    const val TYPES_PHOTOS = "photos"
    const val TYPES_VIDEOS = "videos"
    const val TYPES_ALL = "all"

    // Vorhandene Datei am Ziel
    const val CONFLICT_SKIP = "skip"
    const val CONFLICT_RENAME = "rename"
    const val CONFLICT_REPLACE = "replace"

    /** Abgleichordner von notizi (Vorgabe des Servers); der Server sperrt ihn ohnehin fuer die Dateiablage. */
    const val NOTIZI_ROOT = "notizisync"

    /** Unterordner fuer ein Datum, z. B. "2024/05" (Ortszeit des Geraets). */
    fun dateFolder(mode: String, dateMs: Long, zone: ZoneId = ZoneId.systemDefault()): String {
        if (mode == SUB_NONE || dateMs <= 0) return ""
        val d = Instant.ofEpochMilli(dateMs).atZone(zone).toLocalDate()
        val y = "%04d".format(d.year); val m = "%02d".format(d.monthValue); val day = "%02d".format(d.dayOfMonth)
        return when (mode) { SUB_YEAR -> y; SUB_MONTH -> "$y/$m"; SUB_DAY -> "$y/$m/$day"; else -> "" }
    }

    /**
     * Zielordner einer Datei: Zielordner der Regel, darunter auf Wunsch die Unterordner der Quelle
     * ([relDir], z. B. "WhatsApp Images/Sent"), dann der Datumsordner.
     */
    fun targetDir(target: String, relDir: String, keepStructure: Boolean, subfolders: String, dateMs: Long, zone: ZoneId = ZoneId.systemDefault()): String =
        listOf(normalize(target), if (keepStructure) normalize(relDir) else "", dateFolder(subfolders, dateMs, zone))
            .filter { it.isNotEmpty() }.joinToString("/")

    /** Pfad ohne fuehrende/doppelte Schraegstriche, ohne "." und "..". */
    fun normalize(path: String): String =
        path.replace('\\', '/').split('/').map { it.trim() }.filter { it.isNotEmpty() && it != "." && it != ".." }.joinToString("/")

    /** Darf hier nicht hin: der notizi-Abgleichordner (auch darunter). */
    fun forbiddenTarget(target: String, notiziRoot: String = NOTIZI_ROOT): Boolean {
        val t = normalize(target).lowercase(); val r = normalize(notiziRoot).lowercase()
        return r.isNotEmpty() && (t == r || t.startsWith("$r/"))
    }

    /** Passt die Datei zu den gewaehlten Arten? Fotos und Videos am MIME-Typ, ersatzweise an der Endung. */
    fun typeMatches(types: String, mime: String?, name: String): Boolean {
        val kind = kindOf(mime, name)
        return when (types) { TYPES_PHOTOS -> kind == 1; TYPES_VIDEOS -> kind == 2; else -> true }
    }

    /** 1 = Foto, 2 = Video, 0 = sonstiges. */
    fun kindOf(mime: String?, name: String): Int {
        val m = mime?.lowercase() ?: ""
        if (m.startsWith("image/")) return 1
        if (m.startsWith("video/")) return 2
        return when (name.substringAfterLast('.', "").lowercase()) {
            "jpg", "jpeg", "png", "gif", "webp", "heic", "heif", "dng", "bmp", "avif" -> 1
            "mp4", "mov", "3gp", "mkv", "webm", "avi", "m4v" -> 2
            else -> 0
        }
    }

    /** Versteckte und noch nicht fertige Dateien nie hochladen (".nomedia", ".pending-…", ".trashed-…"). */
    fun ignoredName(name: String): Boolean = name.isEmpty() || name.startsWith(".")

    /** Wartezeit vor dem naechsten Versuch: 30 s, 1 min, 2 min … hoechstens 6 h. */
    fun backoffMs(attempts: Int): Long {
        val n = attempts.coerceIn(1, 20) - 1
        return minOf(30_000L shl minOf(n, 10), 6 * 3600_000L)
    }

    /** So oft wird bei Netz- und Serverfehlern wiederholt, danach bleibt der Eintrag als fehlgeschlagen stehen. */
    const val MAX_ATTEMPTS = 12

    /**
     * HTTP-Fehler, bei denen ein neuer Versuch nichts aendert (Ziel gesperrt, zu gross, abgelehnt).
     * 401 (App-Passwort ungueltig), 507 (Speicher voll), 429/5xx und Netzfehler werden wiederholt.
     */
    fun permanent(code: Int): Boolean = code in setOf(400, 403, 404, 409, 413, 415, 422)

    /** Liegt diese Datei schon am Ziel? Gleicher Name (ohne Gross/Klein) und gleiche Groesse. */
    fun sameRemote(remoteName: String, remoteSize: Long, remoteIsDir: Boolean, name: String, size: Long): Boolean =
        !remoteIsDir && remoteName.equals(name, ignoreCase = true) && remoteSize == size

    /** Relativer Unterordner einer Datei in der Quelle: "DCIM/Camera/Sub/" unter "DCIM/Camera/" -> "Sub". */
    fun relDir(sourceRoot: String, fileDir: String): String {
        val r = normalize(sourceRoot); val d = normalize(fileDir)
        return when {
            r.isEmpty() -> d
            d.equals(r, ignoreCase = true) -> ""
            d.lowercase().startsWith(r.lowercase() + "/") -> d.substring(r.length + 1)
            else -> ""
        }
    }

    // ---------------- Auswahl "anderer Medienordner" ----------------

    /** 0 = bekannter Ordner (Kamera, Screenshots, WhatsApp), 1 = sonstiger, 2 = Kopie im Speicher einer fremden App (Android/media, Android/data). */
    fun folderRank(path: String): Int {
        val p = normalize(path).lowercase() + "/"
        if (PRESET_PATHS.values.flatten().any { p.startsWith(it.lowercase()) }) return 0
        if (p.startsWith("android/media/") || p.startsWith("android/data/")) return 2
        return 1
    }

    /** Anzeigename: letztes Stueck des Pfads ("Camera"). */
    fun folderName(path: String): String = normalize(path).substringAfterLast('/')

    /** Uebergeordneter Pfad, hoechstens die letzten [keep] Stuecke: "…/SofortUpload/Camera". */
    fun folderParent(path: String, keep: Int = 2): String {
        val parts = normalize(path).split('/').dropLast(1).filter { it.isNotEmpty() }
        return if (parts.size <= keep) parts.joinToString("/") else "…/" + parts.takeLast(keep).joinToString("/")
    }

    /** Bekannte zuerst, fremde App-Kopien zuletzt; innerhalb nach Anzahl (absteigend), dann Name. */
    fun <T> sortFolders(list: List<T>, path: (T) -> String, count: (T) -> Int): List<T> =
        list.sortedWith(compareBy<T>({ folderRank(path(it)) }, { -count(it) }, { folderName(path(it)).lowercase() }))

    /**
     * Neu genug fuer eine Regel mit Startdatum [sinceMs] (0 = auch alle vorhandenen Dateien).
     * Es zaehlt das Aufnahmedatum ([takenMs], sonst das Aenderungsdatum der Datei): "Hochladen ab 1. September" meint die Fotos
     * ab dem 1. September, nicht alles, was nach einem Geraetewechsel oder einer Neuindizierung dem Geraet "neu hinzugefuegt"
     * wurde. Dateien, die erst NACH dem Anlegen der Regel auf das Geraet kamen ([addedMs] >= [ruleCreatedMs]), zaehlen immer
     * als neu - auch wenn sie von frueher stammen (kopiert, aus einem Messenger gespeichert).
     */
    fun newEnough(sinceMs: Long, takenMs: Long, modifiedMs: Long, addedMs: Long, ruleCreatedMs: Long): Boolean =
        sinceMs <= 0 || (if (takenMs > 0) takenMs else modifiedMs) >= sinceMs || (ruleCreatedMs > 0 && addedMs >= ruleCreatedMs)
}
