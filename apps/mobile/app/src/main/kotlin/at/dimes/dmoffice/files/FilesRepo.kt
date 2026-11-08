package at.dimes.dmoffice.files

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import at.dimes.dmoffice.upload.ChunkedUpload
import kotlinx.serialization.Serializable
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap

@Serializable
data class FileItem(val path: String, val name: String, val isDir: Boolean, val size: Long = 0, val mime: String? = null, val modified: String? = null,
                    val etag: String? = null, val fileId: Long = 0, val favorite: Boolean = false, val shared: Boolean = false,
                    /** Version 2: ein nur gezeigter Ordner in „Mit mir geteilt“ (sonst null) */
                    val showOnly: Boolean? = null,
                    /** Galerie: weitere Dateien desselben Stapels (RAW zum JPG); nur auf dem Geraet gebildet */
                    @kotlinx.serialization.Transient val stack: List<FileItem> = emptyList()) {
    /** Aenderungszeit in ms (0 = unbekannt). Der Server liefert UTC, teils ohne Zonenangabe. */
    val modifiedMs: Long get() = parseTime(modified)

    /** MIME-Typ fuer Android: vom Server, sonst aus der Endung. */
    val type: String get() = mime?.takeIf { it.isNotBlank() && it != "application/octet-stream" } ?: FilesRepo.guessMime(name)
}

@Serializable
data class Listing(val path: String = "", val usage: Long = 0, val quota: Long = 0, val items: List<FileItem> = emptyList(),
                   /** Alles, was der Benutzer belegt (Mails, Kalender, Kontakte, Notizen, Dateien) - dafuer gilt das Kontingent. */
                   val used: Long = 0,
                   val trash: Long = 0, val versions: Long = 0, val favorite: Boolean = false,
                   /** Version 2: dieser Ordner liegt in einem nur gezeigten Ordner */
                   val showOnly: Boolean = false,
                   /** Aus den gespeicherten Metadaten (ohne Verbindung), nicht frisch vom Server. */
                   @kotlinx.serialization.Transient val cached: Boolean = false)

@Serializable data class Items(val items: List<FileItem> = emptyList())

/** Album der Galerie (Version 2): fest oder smart; mine = eigenes, sonst mit mir geteilt. */
/** Aufnahmedaten eines Fotos (wie /api/files/meta): key ist der Pfad. */
@Serializable data class PhotoMeta(val key: String, val taken: Long? = null, val camera: String? = null, val lens: String? = null,
    val rating: Int? = null, val keywords: String? = null, val width: Int? = null, val height: Int? = null,
    /** Titel und Bildunterschrift (XMP/IPTC bzw. Sidecar-Datei) */
    val title: String? = null, val description: String? = null,
    /** Aufnahmeort (Atlas) */
    val lat: Double? = null, val lon: Double? = null,
    /** Farbmarkierung (red, yellow, green, blue, purple, orange, pink, gray oder ein eigener Name) */
    val label: String? = null,
    /** Markierung: 1 = P, -1 = X, 0 = U */
    val pick: Int? = null,
    /** Aufnahmedaten: Brennweite (mm), Blende, Belichtungszeit (s), ISO */
    val focal: Double? = null, val aperture: Double? = null, val exposure: Double? = null, val iso: Int? = null)
@Serializable data class PhotoMetaList(val items: List<PhotoMeta> = emptyList())

/** Titel, Bildunterschrift, Bewertung, Stichwörter schreiben (POST /api/gallery/info); null = bleibt. */
@Serializable data class PhotoInfoBody(val items: List<String>, val title: String? = null, val description: String? = null, val rating: Int? = null,
    val addKeywords: List<String> = emptyList(), val removeKeywords: List<String> = emptyList(), val pick: Int? = null, val label: String? = null)
@Serializable data class PhotoInfoFailure(val id: String = "", val name: String = "", val error: String = "")
@Serializable data class PhotoInfoResult(val failed: List<PhotoInfoFailure> = emptyList())
/** Galerie-Ordner (externe Speicher, Version 2): Fotos mit Kennung „Quelle/Pfad“, in der App als Pfad „ext:Quelle/Pfad“. */
@Serializable data class ExtPhoto(val id: String, val name: String, val size: Long = 0, val mime: String? = null, val modified: String? = null, val etag: String? = null)
@Serializable data class ExtPhotos(val items: List<ExtPhoto> = emptyList(), val meta: List<PhotoMeta> = emptyList())
/** Zeitleiste der Galerie (Server ab Version 2, /api/gallery/timeline/all): je Foto eine Zeile mit kurzen Feldnamen. */
@Serializable data class TimelineRow(val i: String, val s: Long = 0, val m: Long = 0, val t: String? = null, val e: String? = null, val f: Long = 0,
    val k: Long? = null, val w: Int? = null, val h: Int? = null, val r: Int? = null, val c: String? = null, val l: String? = null, val kw: String? = null,
    val la: Double? = null, val lo: Double? = null, val fv: Int? = null, val ln: Int? = null, val so: Int? = null, val lb: String? = null,
    val ti: String? = null, val de: String? = null,
    /** Brennweite, Blende, Belichtungszeit, ISO */
    val fo: Double? = null, val a: Double? = null, val x: Double? = null, val o: Int? = null)
/** Galerie-Ordner, nur für die Namen der Quelle im Filter */
@Serializable data class GallerySourceName(val id: String, val mount: String = "", val mountName: String? = null, val path: String = "")
@Serializable data class GalleryMountName(val id: String, val name: String = "")
@Serializable data class GallerySources(val sources: List<GallerySourceName> = emptyList(), val mounts: List<GalleryMountName> = emptyList())
@Serializable data class TimelineAll(val items: List<TimelineRow> = emptyList())
/** Galerie nach Quelle, Kategorie oder Tag (/api/gallery/timeline/groups): je Gruppe der Name vom Server und ihre Fotos, in Reihenfolge. */
@Serializable data class TimelineGroup(val l: String = "", val i: List<String> = emptyList())
@Serializable data class TimelineGroups(val groups: List<TimelineGroup> = emptyList())

/** Personen der Galerie (Server: Gallery/GalleryFaces.cs) - Gesichter über die Photo-Engine, auf dem eigenen Server. */
@Serializable data class FacesStatus(val available: Boolean = false, val enabled: Boolean = false, val running: Boolean = false, val overview: FacesOverview? = null,
    val tresor: FacesTresor = FacesTresor())
/** Gesichtsdaten liegen im Tresor: ohne ihn keine Personen, geschlossen keine Übersicht */
@Serializable data class FacesTresor(val exists: Boolean = true, val open: Boolean = true)
@Serializable data class FacesOverview(val persons: List<FacePerson> = emptyList(), val groups: List<FaceGroup> = emptyList(),
    val scanned: Long = 0, val faces: Long = 0, val ignored: Int = 0)
@Serializable data class FacePerson(val id: Long, val name: String, val hidden: Boolean = false, val count: Int = 0, val face: Long? = null)
/** Unbenannte Gruppe; suggest: benannte Person, der sie am ehesten gleicht */
@Serializable data class FaceGroup(val id: String, val count: Int = 0, val face: Long? = null, val suggest: FaceSuggestPerson? = null)
@Serializable data class FaceSuggestPerson(val id: Long, val name: String)
@Serializable data class FacePhotosDto(val items: List<TimelineRow> = emptyList(), val faces: Map<String, List<Long>> = emptyMap(),
    val suggestions: List<FaceSuggestion> = emptyList())
@Serializable data class FaceSuggestion(val face: Long, val path: String = "", val sim: Double = 0.0)
/** Fotos einer Person oder Gruppe: faces = Gesichter je Foto (für „Ist nicht …“) */
data class FacePhotos(val items: List<FileItem>, val faces: Map<String, List<Long>>, val suggestions: List<FaceSuggestion>)
@Serializable data class FacesOnBody(val enabled: Boolean)
@Serializable data class FacesNameBody(val name: String, val group: String? = null, val faces: List<Long>? = null)
@Serializable data class FacesIdsBody(val faces: List<Long>)
@Serializable data class FacesRenameBody(val name: String)
@Serializable data class FacesRejectBody(val person: Long)
@Serializable data class FacesNamed(val person: Long = 0)

@Serializable data class AlbumInfo(val id: Long, val name: String, val kind: String = "fixed", val owner: String = "", val ownerName: String? = null,
    val mine: Boolean = false, val shared: Boolean = false, val count: Int = 0) {
    val fixed: Boolean get() = kind == "fixed"
}
@Serializable data class AlbumItemDto(val key: String, val name: String, val size: Long = 0, val mime: String? = null, val modified: String? = null,
    val etag: String? = null, val mine: Boolean = false)
@Serializable data class AlbumItems(val items: List<AlbumItemDto> = emptyList())
@Serializable data class AlbumNew(val name: String, val kind: String = "fixed")
@Serializable data class CropBody(val x: Float, val y: Float, val w: Float, val h: Float)
@Serializable data class PhotoEditBody(val path: String, val rotate: Int, val crop: CropBody? = null, val overwrite: Boolean, val adjust: Map<String, Float>? = null)
@Serializable data class PhotoAdjustBody(val path: String, val adjust: Map<String, Float>? = null)
@Serializable data class AiAvailable(val available: Boolean = false, val engine: Boolean = false)
/** Vorschlag für die Regler; nur Zahlen zählen (ältere Server schickten zusätzlich isNeutral als Wahrheitswert). */
@Serializable data class AdjustSuggestion(@kotlinx.serialization.SerialName("values") val raw: Map<String, kotlinx.serialization.json.JsonElement> = emptyMap(), val analysis: String? = null) {
    val values: Map<String, Float> get() = raw.mapNotNull { (k, v) -> (v as? kotlinx.serialization.json.JsonPrimitive)?.takeIf { !it.isString }?.content?.toFloatOrNull()?.let { k to it } }.toMap()
}
@Serializable data class PhotoEdited(val path: String)
@Serializable data class AlbumCreated(val id: Long)
@Serializable data class AlbumRename(val name: String)
@Serializable data class AlbumPaths(val paths: List<String>)
@Serializable data class AlbumKeys(val keys: List<String>)

/** Kategorie der Dateiablage (docs/datei-kategorien-konzept.md): verschachtelt (parent), lernt, ordnet neue Dateien automatisch ein.
 *  count: direkt zugeordnete Dateien, unconfirmed: davon automatisch eingeordnete, noch nicht bestaetigte. */
@Serializable data class FileCat(val id: String, val parent: String? = null, val name: String, val color: String? = null, val learn: Boolean = true,
                                 val auto: Boolean = true, val count: Int = 0, val unconfirmed: Int = 0)
@Serializable data class FileTag(val tag: String, val count: Int = 0)
@Serializable data class CatTree(val cats: List<FileCat> = emptyList(), val tags: List<FileTag> = emptyList()) {
    fun kids(parent: String?) = cats.filter { it.parent == parent }
    /** Die Kategorie und alle darunter. */
    fun below(id: String): Set<String> {
        val set = mutableSetOf(id)
        var more = true
        while (more) { more = false; for (c in cats) if (c.parent != null && c.parent in set && set.add(c.id)) more = true }
        return set
    }
    /** Oberkategorien, die naechste zuerst. */
    fun above(id: String): List<String> {
        val by = cats.associateBy { it.id }
        val list = mutableListOf<String>()
        var c = by[id]
        while (c?.parent != null && c.parent in by && c.parent !in list && c.parent != id) { list.add(c.parent!!); c = by[c.parent] }
        return list
    }
    fun fullName(id: String): String { val by = cats.associateBy { it.id }; return (above(id).reversed() + id).joinToString(" › ") { by[it]?.name ?: "?" } }
    fun total(id: String, unconfirmed: Boolean = false) = below(id).sumOf { x -> cats.firstOrNull { it.id == x }?.let { if (unconfirmed) it.unconfirmed else it.count } ?: 0 }
}
@Serializable data class CatAssigned(val id: String, val auto: Boolean = false)
@Serializable data class FileLabels(val cats: List<CatAssigned> = emptyList(), val tags: List<String> = emptyList())
@Serializable data class CatItems(val items: List<FileItem> = emptyList(), val labels: Map<String, FileLabels> = emptyMap())
@Serializable data class LabelsOf(val items: Map<String, FileLabels> = emptyMap())
@Serializable data class LabelsSet(val paths: List<String>, val add: List<String> = emptyList(), val remove: List<String> = emptyList(),
                                   val addTags: List<String> = emptyList(), val removeTags: List<String> = emptyList())
@Serializable data class CatNew(val name: String, val parent: String? = null, val color: String? = null)
@Serializable data class CatChange(val name: String? = null, val learn: Boolean? = null, val auto: Boolean? = null)
@Serializable data class RecentItem(val item: FileItem, val used: String? = null)
@Serializable data class RecentList(val items: List<RecentItem> = emptyList())
@Serializable data class TrashItem(val id: Long, val path: String, val name: String, val isDir: Boolean = false, val size: Long = 0, val deleted: String? = null, val mime: String? = null)
@Serializable data class TrashList(val days: Int = 30, val usage: Long = 0, val items: List<TrashItem> = emptyList())
@Serializable data class FileVersion(val id: Long, val size: Long = 0, val etag: String? = null, val modified: String? = null, val saved: String? = null)
@Serializable data class VersionList(val items: List<FileVersion> = emptyList())
@Serializable data class FileLink(val id: String, val token: String, val mode: String = "read", val expires: String? = null, val hasPassword: Boolean = false,
                                  val created: String? = null, val downloads: Long = 0, val maxDownloads: Long? = null, val expired: Boolean = false, val item: FileItem? = null)
@Serializable data class LinkList(val items: List<FileLink> = emptyList())
@Serializable data class ContentHit(val path: String, val name: String, val size: Long = 0, val mime: String? = null, val modified: String? = null, val method: String? = null, val snippet: String = "")
@Serializable data class ContentResult(val enabled: Boolean = false, val items: List<ContentHit> = emptyList())
@Serializable data class FileInfo(val item: FileItem, val size: Long = 0, val files: Int = 0, val folders: Int = 0, val versions: List<FileVersion> = emptyList(), val links: List<FileLink> = emptyList())

fun parseTime(s: String?): Long {
    if (s.isNullOrBlank()) return 0
    return try { java.time.OffsetDateTime.parse(s).toInstant().toEpochMilli() } catch (_: Exception) {
        try { java.time.LocalDateTime.parse(s).toInstant(java.time.ZoneOffset.UTC).toEpochMilli() } catch (_: Exception) { 0 }
    }
}

/**
 * Dateiablage von dmOffice ueber /api/files (Web-API mit App-Passwort, im Umschlag wie alles andere).
 * Haelt die zuletzt geladenen Ordnerinhalte kurz im Speicher, damit der DocumentsProvider nicht fuer
 * jede Einzelabfrage zum Server muss. Geoeffnete Dateien liegen im Cache unter files/.
 */
class FilesRepo(private val context: Context) {
    private val listings = ConcurrentHashMap<String, Pair<Long, Listing>>()

    /** Offline verfuegbare Ordner (lokale Kopie mit Abgleich). */
    val offline by lazy { Offline(context, this) }

    private fun api(): ApiClient = App.get(context).api ?: throw ApiException(401, "dmOffice ist nicht gekoppelt")

    /** Server ohne Datei-Tickets: verstaendliche Meldung statt "tickets". */
    private fun <T> tooOld(block: () -> T): T = try { block() } catch (_: ApiClient.TicketsUnsupported) {
        throw java.io.IOException(context.getString(at.dimes.dmoffice.R.string.files_err_server_old))
    }

    /** Ordnerinhalt: in offline verfuegbaren Ordnern aus der lokalen Kopie, sonst vom Server. */
    fun list(path: String): Listing {
        // Tresor: auf dem Gerät entschlüsselt, nichts davon in Room oder den Offline-Ordnern (kein Klartext auf dem Gerät)
        val l = if (isTresor(path)) listTresor(path) else if (offline.ready(path)) offline.listLocal(path) else try { listServer(path) } catch (e: java.io.IOException) {
            // Ohne Netz: zuletzt gesehene Metadaten, sonst wenigstens den Weg zu den Offline-Ordnern (HTTP-Fehler bleiben Fehler)
            if (e is ApiException) throw e
            metaListing(path) ?: offline.pathTo(path) ?: throw e
        }
        listings[path] = System.currentTimeMillis() to l
        return l
    }

    /**
     * Ordner im Tresor: vom Server entschlüsselt. Ohne Netz aus der verschlüsselten Offline-Kopie, auf dem Gerät entschlüsselt
     * (Stufe 7b, [TresorOffline]).
     */
    private fun listTresor(path: String): Listing = try {
        listServer(path).also { syncTresorSoon(false) }
    } catch (e: java.io.IOException) {
        if (e is ApiException) throw e
        TresorOffline.list(context, path) ?: throw e
    }

    @Volatile private var tresorSyncAt = 0L
    /** Offline verfügbare Ordner im Tresor im Hintergrund abgleichen ([now]: nach einer Änderung, sonst höchstens alle 5 Minuten). */
    fun syncTresorSoon(now: Boolean) {
        if (TresorOffline.roots.value.isEmpty()) return
        val t = System.currentTimeMillis()
        if (!now && t - tresorSyncAt < 5 * 60_000) return
        tresorSyncAt = t
        Thread { try { Thread.sleep(if (now) 1500 else 0); TresorOffline.sync(context, api()) } catch (e: Exception) { android.util.Log.w("FilesRepo", "Tresor offline: " + e.message) } }.start()
    }

    /** Ordner im Tresor offline verfügbar machen bzw. nicht mehr. */
    fun tresorOffline(path: String, on: Boolean) { if (on) TresorOffline.mark(context, api(), path) else TresorOffline.unmark(context, try { api() } catch (_: Exception) { null }, path) }

    /** Ordnerinhalt vom Server, Ordner zuerst, dann nach Namen. */
    fun listServer(path: String): Listing {
        val a = api()
        val l: Listing = a.get(a.url("/api/files", "path" to path))
        rememberShown(path, l)
        offline.rememberUsage(l.usage, l.quota)
        if (!isTresor(path)) rememberMeta(path, l.items)
        return l.copy(items = l.items.sortedWith(compareBy<FileItem>({ !it.isDir }, { it.name.lowercase() })))
    }

    // ---------------- Nur gezeigt (Version 2) ----------------

    @Serializable data class SharedMount(val path: String = "", val name: String = "", val owner: String = "", val canWrite: Boolean = false, val showOnly: Boolean = false)
    @Serializable private data class SharedMounts(val items: List<SharedMount> = emptyList())

    @Serializable data class ShareMember(val id: String, val readOnly: Boolean = false, val level: String? = null)
    @Serializable private data class ShareOf(val members: List<ShareMember> = emptyList())
    @Serializable private data class SharesOf(val items: List<ShareOf> = emptyList())

    /** Mit Personen geteilt: Stufe je Person (show, send, share) eines eigenen Ordners oder einer Datei. */
    fun shareLevels(path: String): Map<String, String> {
        val a = api()
        return a.get<SharesOf>(a.url("/api/files/shares", "path" to path)).items.firstOrNull()?.members.orEmpty()
            .associate { it.id to (it.level ?: if (it.readOnly) "send" else "share") }
    }

    /** Stufen setzen (fehlt = kein Zugriff, leer hebt die Freigabe auf). */
    fun setShareLevels(path: String, levels: Map<String, String>) {
        val a = api()
        val body = kotlinx.serialization.json.buildJsonObject {
            put("path", kotlinx.serialization.json.JsonPrimitive(path))
            put("members", kotlinx.serialization.json.JsonArray(levels.keys.map { kotlinx.serialization.json.JsonPrimitive(it) }))
            put("readOnly", kotlinx.serialization.json.JsonArray(levels.filterValues { it != "share" }.keys.map { kotlinx.serialization.json.JsonPrimitive(it) }))
            put("showOnly", kotlinx.serialization.json.JsonArray(levels.filterValues { it == "show" }.keys.map { kotlinx.serialization.json.JsonPrimitive(it) }))
        }
        a.putText(a.url("/api/files/shares"), body.toString())
    }

    /** Ordner, die andere mit mir teilen (liegen unter „Mit mir geteilt“). */
    fun sharedWithMe(): List<SharedMount> {
        val a = api()
        return a.get<SharedMounts>(a.url("/api/files/shared")).items.also { m -> shownRoots = m.filter { it.showOnly }.map { it.path }.toSet(); shownAt = System.currentTimeMillis() }
    }

    /** Wurzeln der nur gezeigten Ordner und wann zuletzt vom Server geholt. */
    @Volatile private var shownRoots: Set<String> = emptySet()
    @Volatile private var shownAt = 0L

    private fun rememberShown(path: String, l: Listing) {
        var roots = shownRoots
        for (i in l.items) if (i.showOnly == true) roots = roots + i.path else if (i.isDir) roots = roots - i.path
        if (l.showOnly && roots.none { path == it || path.startsWith("$it/") }) roots = roots + path
        shownRoots = roots
    }

    /** Liegt der Pfad in einem Ordner, den mir jemand nur zeigt? Ohne Netz aufrufen (Oberflaeche): nur das Bekannte. */
    fun isShownCached(path: String): Boolean = shownRoots.any { path == it || path.startsWith("$it/") }

    /** Wie [isShownCached], holt die Liste aber hoechstens jede Minute frisch (nicht im Hauptthread aufrufen). */
    fun isShown(path: String): Boolean {
        val now = System.currentTimeMillis()
        if (now - shownAt > 60_000) {
            try {
                val a = api()
                val m: SharedMounts = a.get(a.url("/api/files/shared"))
                shownRoots = m.items.filter { it.showOnly }.map { it.path }.toSet()
                shownAt = now
            } catch (e: Exception) { android.util.Log.w("FilesRepo", "Gezeigte Ordner: " + e.message) }
        }
        return isShownCached(path)
    }

    // ---------------- Metadaten in Room ----------------

    private val meta get() = App.get(context).db.fileMeta()

    private fun rememberMeta(dir: String, items: List<FileItem>) {
        try {
            meta.replaceChildren(dir, items.map { at.dimes.dmoffice.data.FileMetaEntity(it.path, dir, it.name, it.isDir, it.size, it.mime, it.modified, it.etag, it.fileId, it.favorite, it.shared) })
        } catch (e: Exception) { android.util.Log.w("FilesRepo", "Metadaten: " + e.message) }
    }

    /** Zuletzt gesehener Inhalt eines Ordners aus Room (null = nie gesehen). */
    fun metaListing(path: String): Listing? = try {
        val rows = meta.childrenBlocking(path)
        if (rows.isEmpty() && (path.isEmpty() || meta.isKnownDir(path) == 0)) null
        else Listing(path, offline.state.value.usage, offline.state.value.quota, rows.map { FileItem(it.path, it.name, it.isDir, it.size, it.mime, it.modified, it.etag, it.fileId, it.favorite, it.shared) }
            .sortedWith(compareBy<FileItem>({ !it.isDir }, { it.name.lowercase() })), cached = true)
    } catch (_: Exception) { null }

    private fun forgetMeta(path: String) {
        try { meta.deleteTree(path, path.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "/%") } catch (_: Exception) { }
    }

    // ---------------- Bereiche wie im Web ----------------

    fun recent(): List<RecentItem> { val a = api(); return a.get<RecentList>(a.url("/api/files/recent")).items }
    fun favorites(): List<FileItem> { val a = api(); return a.get<Items>(a.url("/api/files/favorites")).items }
    /** Alle Bilder (Galerie), neueste zuerst. */
    /** Galerie: zuletzt geladene Liste und Bildangaben - beim naechsten Oeffnen sofort da, dann frisch vom Server */
    @Volatile var photosCache: List<FileItem>? = null; private set
    @Volatile var photoMetaCache: Map<String, PhotoMeta> = emptyMap(); private set

    /** Bildangaben der Galerie-Ordner vom letzten Laden (kommen mit der Liste) */
    @Volatile private var extMeta: Map<String, PhotoMeta> = emptyMap()

    /** Zeile der Zeitleiste als Eintrag (Pfad wie in der Galerie, also auch für Betrachter und Vorschaubilder). */
    private fun rowItem(r: TimelineRow) = FileItem(r.i, r.i.substring(r.i.lastIndexOf('/') + 1), false, r.s, r.t,
        java.time.Instant.ofEpochMilli(r.m).toString(), r.e, r.f, r.fv == 1, r.ln == 1, if (r.so == 1) true else null)

    // Personen (Gesichter)
    fun facesStatus(): FacesStatus? = try { val a = api(); a.get(a.url("/api/gallery/faces")) } catch (e: ApiException) { if (e.code == 404) null else throw e }
    fun facesEnable(on: Boolean) { val a = api(); a.putText(a.url("/api/gallery/faces"), a.json.encodeToString(FacesOnBody.serializer(), FacesOnBody(on))) }
    private fun facePhotos(url: String): FacePhotos {
        val a = api()
        val r = a.get<FacePhotosDto>(a.url(url))
        return FacePhotos(r.items.map(::rowItem), r.faces, r.suggestions)
    }
    fun personPhotos(id: Long) = facePhotos("/api/gallery/faces/persons/$id")
    fun groupPhotos(id: String) = facePhotos("/api/gallery/faces/groups/" + java.net.URLEncoder.encode(id, "UTF-8"))
    /** Gruppe benennen (gleicher Name: dieselbe Person) - liefert die Person. */
    fun nameGroup(group: String, name: String): Long { val a = api(); return a.postFor<FacesNameBody, FacesNamed>(a.url("/api/gallery/faces/name"), FacesNameBody(name, group = group)).person }
    fun renamePerson(id: Long, name: String): Long {
        val a = api()
        return a.json.decodeFromString(FacesNamed.serializer(), a.putText(a.url("/api/gallery/faces/persons/$id"), a.json.encodeToString(FacesRenameBody.serializer(), FacesRenameBody(name)))).person
    }
    fun removePerson(id: Long) { val a = api(); a.delete(a.url("/api/gallery/faces/persons/$id")) }
    /** Titel, Bildunterschrift, Bewertung, Stichwörter in die Fotos schreiben; zurück: die Fotos, bei denen es nicht ging. */
    fun photoInfo(body: PhotoInfoBody): List<PhotoInfoFailure> { val a = api(); return a.postFor<PhotoInfoBody, PhotoInfoResult>(a.url("/api/gallery/info"), body).failed }

    fun confirmFaces(person: Long, faces: List<Long>) { val a = api(); a.post(a.url("/api/gallery/faces/persons/$person/confirm"), FacesIdsBody(faces)) }
    fun rejectFaces(person: Long, faces: List<Long>) { val a = api(); a.post(a.url("/api/gallery/faces/persons/$person/reject"), FacesIdsBody(faces)) }
    fun rejectGroup(group: String, person: Long) { val a = api(); a.post(a.url("/api/gallery/faces/groups/" + java.net.URLEncoder.encode(group, "UTF-8") + "/reject"), FacesRejectBody(person)) }
    fun ignoreGroup(group: String) { val a = api(); a.postText(a.url("/api/gallery/faces/groups/" + java.net.URLEncoder.encode(group, "UTF-8") + "/ignore"), "{}") }
    /** Ausgeblendete wieder zeigen: eine Gruppe (Rückgängig) oder alle (group = null). */
    fun unignore(group: String?) { val a = api(); a.delete(a.url("/api/gallery/faces/ignored", "group" to group)) }
    /** Ausschnitt eines Gesichts (quadratisch). */
    fun faceCrop(id: Long, size: Int): android.graphics.Bitmap? = try {
        val a = api()
        a.getBytes(a.url("/api/gallery/faces/crop/$id", "size" to size)).first.let { android.graphics.BitmapFactory.decodeByteArray(it, 0, it.size) }
    } catch (e: ApiException) { if (e.code == 404) null else throw e }

    /** Bildangaben kamen schon mit der Liste (Zeitleiste) - dann kein eigener Abruf */
    @Volatile private var metaWithList = false

    /**
     * Galerie: eigene Fotos und die aus den Galerie-Ordnern (wie im Web) - sonst fehlen in der App die älteren Jahre.
     * Neuer Server: eine kompakte Liste samt Bildangaben aus der Zeitleiste; älterer: Suche + Galerie-Ordner wie bisher.
     */
    fun photoGroups(group: String): List<TimelineGroup> { val a = api(); return a.get<TimelineGroups>(a.url("/api/gallery/timeline/groups", "group" to group)).groups }

    fun photos(): List<FileItem> {
        val a = api()
        try {
            val rows = a.get<TimelineAll>(a.url("/api/gallery/timeline/all")).items
            val meta = HashMap<String, PhotoMeta>(rows.size)
            val list = rows.mapNotNull { r ->
                val name = r.i.substring(r.i.lastIndexOf('/') + 1)
                if (!(r.t ?: "").startsWith("image/") && !FileViews.hasThumb(FileItem(name, name, false, mime = r.t))) return@mapNotNull null
                if (r.k != null || r.c != null || r.w != null || r.kw != null || r.r != null || r.la != null || r.lb != null || r.ti != null || r.de != null || r.a != null)
                    meta[r.i] = PhotoMeta(r.i, r.k, r.c, r.l, r.r, r.kw, r.w, r.h, title = r.ti, description = r.de, lat = r.la, lon = r.lo, label = r.lb,
                        focal = r.fo, aperture = r.a, exposure = r.x, iso = r.o)
                rowItem(r)
            }
            extMeta = meta
            metaWithList = true
            return list.also { photosCache = it }
        } catch (e: ApiException) { if (e.code != 404) throw e }   // älterer Server ohne Zeitleiste
        metaWithList = false
        val own = a.get<Items>(a.url("/api/files/search", "q" to "", "type" to FileViews.IMAGE, "photos" to true)).items
        val ext = try {
            val r = a.get<ExtPhotos>(a.url("/api/gallery/external"))
            extMeta = r.meta.associateBy { it.key }
            r.items.filter { (it.mime ?: "").startsWith("image/") || FileViews.hasThumb(FileItem(it.name, it.name, false, mime = it.mime)) }
                .map { FileItem(EXT + it.id, it.name, false, it.size, it.mime, it.modified, it.etag) }
        } catch (e: ApiException) { if (e.code == 404) emptyList() else throw e }   // Server ohne Galerie-Ordner
        return (own + ext).sortedByDescending { it.modifiedMs }.also { photosCache = it }
    }
    /** Ordner, deren Bilder die Ansicht Fotos nicht zeigt (wie im Web). */
    fun photoExcludes(): List<String> { val a = api(); return a.get(a.url("/api/files/photo-excludes")) }
    fun setPhotoExcludes(folders: List<String>) {
        val a = api()
        a.putText(a.url("/api/files/photo-excludes"), kotlinx.serialization.json.buildJsonObject {
            put("folders", kotlinx.serialization.json.JsonArray(folders.map { kotlinx.serialization.json.JsonPrimitive(it) }))
        }.toString())
    }
    /** Suche nach Namen unter [under] ("" = ueberall), optional nur eine Art. */
    fun search(q: String, type: String?, under: String): List<FileItem> {
        val a = api(); return a.get<Items>(a.url("/api/files/search", "q" to q, "path" to under, "type" to type)).items
    }
    /** Inhaltssuche (Volltextindex des Servers). */
    fun searchContent(q: String, under: String): ContentResult { val a = api(); return a.get(a.url("/api/files/search/content", "q" to q, "path" to under, "limit" to 200)) }
    fun info(path: String): FileInfo { val a = api(); return a.get(a.url("/api/files/info", "path" to path)) }

    // ---------------- Office (Editor des Office-Servers) ----------------

    @Volatile private var officeCfg: Pair<Long, OfficeConfig>? = null

    /** Office-Server da und welche Endungen? Fuenf Minuten gemerkt; ohne Verbindung bzw. bei Fehler: aus. */
    fun officeConfig(maxAgeMs: Long = 5 * 60_000): OfficeConfig {
        officeCfg?.let { (t, c) -> if (System.currentTimeMillis() - t < maxAgeMs) return c }
        val c = try { val a = api(); a.get<OfficeConfig>(a.url("/api/office/config")) } catch (e: Exception) {
            android.util.Log.w("FilesRepo", "Office: " + e.message); return officeCfg?.second ?: OfficeConfig()
        }
        officeCfg = System.currentTimeMillis() to c
        return c
    }

    /** Sitzung fuer den Editor (Adresse und Token bzw. api.js und Konfiguration). */
    fun officeOpen(path: String, view: Boolean): OfficeSession { val a = api(); return a.postFor(a.url("/api/office/open"), OfficeOpenBody(path, view, mobile = true)) }

    /** Leeres Dokument (docx, xlsx, pptx) anlegen; liefert den Pfad. */
    fun officeNew(dir: String, kind: String, name: String): String {
        val a = api()
        val r: OfficeNewResult = a.postFor(a.url("/api/office/new"), OfficeNewBody(dir, kind, name))
        listings.remove(dir)
        return r.path
    }

    fun setFavorite(path: String, on: Boolean) {
        val a = api()
        a.postText(a.url("/api/files/favorite"), a.json.encodeToString(FavBody.serializer(), FavBody(path, on)))
        try { meta.setFavorite(path, on) } catch (_: Exception) { }
        invalidate(parent(path))
    }

    /** Mehrere in den Papierkorb; liefert die Eintraege dort (fuer Rueckgaengig). */
    fun deleteMany(paths: List<String>): List<Long> {
        val a = api()
        val r = a.postText(a.url("/api/files/delete"), a.json.encodeToString(PathsBody.serializer(), PathsBody(paths)))
        for (p in paths) {
            invalidate(parent(p), p); forgetMeta(p)
            if (offline.state.value.roots.keys.any { Offline.inside(it, p) }) offline.drop(p)
            afterChange(p)
        }
        return try { a.json.decodeFromString(TrashIds.serializer(), r).trash } catch (_: Exception) { emptyList() }
    }

    /** Verschieben/Kopieren mit Konfliktregel (fail, rename, replace, skip); liefert den Pfad am Ziel (null = uebersprungen). */
    fun transfer(from: String, to: String, copy: Boolean, conflict: String = "fail"): String? {
        val a = api()
        val r = a.postText(a.url("/api/files/move"), a.json.encodeToString(MoveBody.serializer(), MoveBody(from, to, copy, conflict)))
        invalidate(parent(from), parent(to), from)
        if (!copy) forgetMeta(from)
        if (!copy && offline.state.value.roots.keys.any { Offline.inside(it, from) }) offline.moved(from, to)
        if (!copy) afterChange(from)
        afterChange(to)
        return try { a.json.decodeFromString(MoveResult.serializer(), r).let { if (it.skipped) null else it.path ?: to } } catch (_: Exception) { to }
    }

    // Papierkorb
    fun trash(): TrashList { val a = api(); return a.get(a.url("/api/files/trash")) }
    fun restore(ids: List<Long>) { val a = api(); a.postText(a.url("/api/files/trash/restore"), a.json.encodeToString(IdsBody.serializer(), IdsBody(ids))); listings.clear() }
    fun purge(ids: List<Long>) { val a = api(); a.postText(a.url("/api/files/trash/delete"), a.json.encodeToString(IdsBody.serializer(), IdsBody(ids))) }
    fun emptyTrash() { val a = api(); a.postText(a.url("/api/files/trash/empty"), null) }

    // Fruehere Versionen
    fun versions(path: String): List<FileVersion> { val a = api(); return a.get<VersionList>(a.url("/api/files/versions", "path" to path)).items }
    fun restoreVersion(path: String, id: Long) {
        val a = api(); a.postText(a.url("/api/files/versions/restore"), a.json.encodeToString(VersionBody.serializer(), VersionBody(path, id))); invalidate(parent(path))
    }
    /** Groesse der Version unbekannt: immer am Umschlag vorbei (mit Ticket); ein Server ohne Tickets liefert sie im Umschlag. */
    fun downloadVersion(path: String, id: Long, dest: File) = tooOld { api().downloadFile(path, dest, big = true, version = id, envelopeOk = true) }

    // Oeffentliche Links
    fun links(path: String? = null): List<FileLink> { val a = api(); return a.get<LinkList>(a.url("/api/files/links", "path" to path)).items }
    fun createLink(path: String, mode: String, expires: String?, password: String?, maxDownloads: Int): FileLink {
        val a = api()
        val l: FileLink = a.json.decodeFromString(a.postText(a.url("/api/files/links"), a.json.encodeToString(LinkBody.serializer(), LinkBody(path, mode, expires, password, maxDownloads))))
        invalidate(parent(path))
        return l
    }
    fun updateLink(l: FileLink, mode: String, expires: String?, password: String?, maxDownloads: Int) {
        val a = api(); a.putText(a.url("/api/files/links/" + l.id), a.json.encodeToString(LinkBody.serializer(), LinkBody(null, mode, expires, password, maxDownloads)))
    }
    fun deleteLink(id: String) { val a = api(); a.delete(a.url("/api/files/links/$id")) }
    /** Adresse fuer andere: /s/<token> auf dem Server. */
    fun publicUrl(l: FileLink): String = (App.get(context).prefs.account.value?.baseUrl ?: "") + "/s/" + l.token

    // ---------------- Alben (Version 2) ----------------
    // Fotos eines Albums haben den Pfad "alb:<Album>/<Schluessel>" - Vorschau und Original kommen ueber das Album.

    /** Aufnahmedaten aller Fotos (Galerie: Aufnahmedatum, Filter). */
    fun photoMeta(): Map<String, PhotoMeta> {
        if (metaWithList) return extMeta.also { photoMetaCache = it }
        val a = api(); return (a.get<PhotoMetaList>(a.url("/api/files/meta")).items.associateBy { it.key } + extMeta).also { photoMetaCache = it }
    }

    /** Namen der Galerie-Ordner („Storage › RAW › PRI“) je Quelle „ext:<Kennung>“ - für den Filter nach Quelle. */
    fun gallerySourceNames(): Map<String, String> = try {
        val a = api()
        val d = a.get<GallerySources>(a.url("/api/gallery/sources"))
        d.sources.associate { s -> "ext:" + s.id to (s.mountName ?: d.mounts.firstOrNull { it.id == s.mount }?.name ?: s.mount) + (if (s.path.isEmpty()) "" else " › " + s.path.split('/').joinToString(" › ")) }
    } catch (_: Exception) { emptyMap() }

    fun albums(): List<AlbumInfo> { val a = api(); return a.get(a.url("/api/gallery/albums")) }
    /** Fotos eines Albums als Eintraege der Galerie und welche davon mir gehoeren (die darf ich herausnehmen). */
    fun albumItems(id: Long): Pair<List<FileItem>, Set<String>> {
        val a = api()
        val r = a.get<AlbumItems>(a.url("/api/gallery/albums/$id/items")).items
        val list = r.map { FileItem("$ALB$id/${it.key}", it.name, false, it.size, it.mime, it.modified, it.etag) }
        return list to r.filter { it.mine }.map { "$ALB$id/${it.key}" }.toSet()
    }
    /** Foto drehen und zuschneiden (Server rechnet aus dem Original, Metadaten bleiben); Antwort: Pfad des Ergebnisses. */
    fun photoEdit(path: String, rotate: Int, crop: List<Float>?, overwrite: Boolean, adjust: Map<String, Float>? = null): String {
        val a = api()
        return a.postFor<PhotoEditBody, PhotoEdited>(a.url("/api/files/photo-edit"), PhotoEditBody(path, rotate, crop?.let { CropBody(it[0], it[1], it[2], it[3]) }, overwrite, adjust)).path
    }
    /** Vorschau mit Reglern (verkleinert, aufrecht) als JPEG. */
    fun photoPreview(path: String, adjust: Map<String, Float>): ByteArray { val a = api(); return a.postForBytes(a.url("/api/files/photo-adjust/preview"), PhotoAdjustBody(path, adjust)) }
    fun photoAiAvailable(): AiAvailable = try { val a = api(); a.get<AiAvailable>(a.url("/api/files/photo-adjust/ai")) } catch (_: Exception) { AiAvailable() }
    /** Vorschlag: from = "suggest" (KI) oder "engine" (Photo-Engine, ohne KI). */
    fun photoSuggest(path: String, from: String = "suggest"): AdjustSuggestion { val a = api(); return a.postFor(a.url("/api/files/photo-adjust/$from"), PhotoAdjustBody(path)) }
    fun createAlbum(name: String): Long { val a = api(); return a.postFor<AlbumNew, AlbumCreated>(a.url("/api/gallery/albums"), AlbumNew(name)).id }
    fun renameAlbum(id: Long, name: String) { val a = api(); a.putText(a.url("/api/gallery/albums/$id"), a.json.encodeToString(AlbumRename.serializer(), AlbumRename(name))) }
    /** Eigenes Album loeschen bzw. ein geteiltes verlassen. */
    fun deleteAlbum(id: Long) { val a = api(); a.delete(a.url("/api/gallery/albums/$id")) }
    fun addToAlbum(id: Long, paths: List<String>) { val a = api(); a.post(a.url("/api/gallery/albums/$id/add"), AlbumPaths(paths)) }
    fun removeFromAlbum(id: Long, items: List<FileItem>) { val a = api(); a.post(a.url("/api/gallery/albums/$id/remove"), AlbumKeys(items.map { albumKey(it.path) })) }

    // Kategorien und Tags der eigenen Dateien (wie im Web, nicht ueber WebDAV)
    fun categories(): CatTree { val a = api(); return a.get(a.url("/api/files/categories")) }
    fun categoryItems(id: String): CatItems { val a = api(); return a.get(a.url("/api/files/categories/" + Uri.encode(id) + "/items")) }
    fun tagItems(tag: String): CatItems { val a = api(); return a.get(a.url("/api/files/tags/items?tag=" + Uri.encode(tag))) }
    fun labelsOf(paths: List<String>): Map<String, FileLabels> { val a = api(); return a.postFor<AlbumPaths, LabelsOf>(a.url("/api/files/labels/of"), AlbumPaths(paths)).items }
    fun setLabels(body: LabelsSet) { val a = api(); a.post(a.url("/api/files/labels"), body) }
    fun createCategory(name: String, parent: String?, color: String?): FileCat { val a = api(); return a.postFor<CatNew, FileCat>(a.url("/api/files/categories"), CatNew(name, parent, color)) }
    fun changeCategory(id: String, change: CatChange) { val a = api(); a.putText(a.url("/api/files/categories/" + Uri.encode(id)), a.json.encodeToString(CatChange.serializer(), change)) }
    fun deleteCategory(id: String) { val a = api(); a.delete(a.url("/api/files/categories/" + Uri.encode(id))) }
    /** Vorschlag fuer den Anfang (nur bei leerer Liste). */
    fun categorySuggestion() { val a = api(); a.post(a.url("/api/files/categories/suggestion"), AlbumKeys(emptyList())) }

    // Vorschaubilder und Streamen
    /** Vorschaubild vom Server (WebP), [size] 256 oder 1600; null = keines. */
    fun thumb(item: FileItem, size: Int): ByteArray? = try {
        val a = api()
        if (isChat(item.path)) a.getBytes(a.url("/api/chat/messages/${chatRef(item.path).first}/refs/${chatRef(item.path).second}/thumb", "size" to size)).first
        else if (isAlbum(item.path)) a.getBytes(a.url("/api/gallery/albums/${albumId(item.path)}/thumb", "key" to albumKey(item.path), "size" to size, "v" to item.etag)).first
        else if (isExt(item.path)) a.getBytes(a.url("/api/gallery/external/thumb", "id" to item.path.removePrefix(EXT), "size" to size, "v" to item.etag)).first
        else a.getBytes(a.url("/api/files/thumb", "path" to item.path, "size" to size, "v" to item.etag)).first
    } catch (e: ApiException) { if (e.code == 404 || e.code == 400 || e.code == 415) null else throw e }

    /**
     * Adresse und Kopfzeilen zum Streamen (Audio/Video, direkt ueber TLS mit Bereichsanfragen): mit Umschlag ein Ticket
     * nur fuer diese Datei, nie das App-Passwort. Geht ins Netz, also nicht im Hauptthread.
     */
    fun streamUrl(item: FileItem): Pair<String, Map<String, String>> = tooOld {
        val a = api()
        if (isExt(item.path)) a.streamRequest(a.url("/api/gallery/external/file", "id" to item.path.removePrefix(EXT), "inline" to true))
        else a.streamRequest(a.url("/api/files/download", "path" to item.path, "inline" to true))
    }

    /** Zuletzt geladener Inhalt eines Ordners samt Alter in ms, ohne Netz. */
    fun cached(path: String): Pair<Long, Listing>? = listings[path]?.let { (t, l) -> (System.currentTimeMillis() - t) to l }

    /** Inhalt aus dem Speicher, wenn juenger als [maxAgeMs], sonst vom Server. */
    fun listCached(path: String, maxAgeMs: Long): Listing =
        cached(path)?.takeIf { it.first <= maxAgeMs }?.second ?: list(path)

    /** Eintrag zu einem Pfad (ueber den Elternordner); "" ist die Wurzel. */
    fun find(path: String, maxAgeMs: Long = 10_000): FileItem? {
        if (path.isEmpty()) return FileItem("", "", true)
        val parent = parent(path)
        listCached(parent, maxAgeMs).items.firstOrNull { it.path == path }?.let { return it }
        // Eben angelegt oder umbenannt: einmal frisch nachsehen
        return list(parent).items.firstOrNull { it.path == path }
    }

    fun invalidate(vararg dirs: String) {
        for (d in dirs) listings.remove(d)
        // Änderung im Tresor: die Offline-Kopie nachziehen
        if (dirs.any { isTresor(it) && TresorOffline.covers(it) }) syncTresorSoon(true)
    }

    /** Tresor geschlossen: geladene Dateien, Ordnerinhalte und Vorschaubilder daraus vergessen. */
    fun clearTresorCache() {
        File(context.cacheDir, "tresor").deleteRecursively()
        listings.keys.filter { isTresor(it) }.forEach { listings.remove(it) }
        Thumbs.forgetTresor()
        TresorOffline.forgetKey()
    }

    /** Gespeicherten Ordnerinhalt als frisch werten (Nachladen fehlgeschlagen: nicht sofort wieder versuchen). */
    fun touch(path: String) { listings[path]?.let { listings[path] = System.currentTimeMillis() to it.second } }

    /** Datei zum Lesen: die lokale Kopie eines Offline-Ordners, sonst in den Cache geladen (gleiche Groesse und Zeit wird wiederverwendet). */
    /**
     * Kleine Fassung eines Fotos zum Teilen (auch RAW): die große Vorschau des Servers (1600 px) als JPEG.
     * Null bei allem anderen oder wenn es keine Vorschau gibt - dann wird das Original geteilt.
     */
    fun shareCopy(item: FileItem): File? {
        val raw = item.name.substringAfterLast('.', "").lowercase() in RAW_EXT
        if (!item.type.startsWith("image/") && !raw || item.type == "image/gif" || item.type == "image/svg+xml") return null
        val bytes = thumb(item, 1600) ?: return null
        val bmp = android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size) ?: return null
        val dir = File(context.cacheDir, "share/" + hash(item.path)).apply { mkdirs() }
        val f = File(dir, safeName(item.name.substringBeforeLast('.')) + ".jpg")
        try { f.outputStream().use { bmp.compress(android.graphics.Bitmap.CompressFormat.JPEG, 85, it) } } finally { bmp.recycle() }
        return f
    }

    fun cacheFile(item: FileItem): File {
        if (offline.ready(item.path)) offline.local(item.path).takeIf { it.isFile }?.let { return it }
        // Aus dem Tresor in einen eigenen Ordner, den das Schließen des Tresors leert
        val dir = File(context.cacheDir, (if (isTresor(item.path)) "tresor/" else "files/") + hash(item.path)).apply { mkdirs() }
        val f = File(dir, safeName(item.name))
        val ms = item.modifiedMs
        if (f.exists() && f.length() == item.size && ms > 0 && f.lastModified() == ms) return f
        val tmp = File(dir, f.name + ".part")
        try {
            if (isTresor(item.path)) decryptTresor(item, tmp) else downloadServer(item, tmp)
            if (!tmp.renameTo(f)) { tmp.copyTo(f, overwrite = true); tmp.delete() }
            if (ms > 0) f.setLastModified(ms)
        } finally { tmp.delete() }
        return f
    }

    /** Datei aus dem Tresor: vom Server entschlüsselt; ohne Netz aus der Offline-Kopie, auf dem Gerät entschlüsselt. */
    private fun decryptTresor(item: FileItem, dest: File) {
        try { downloadServer(item, dest) }
        catch (e: java.io.IOException) { if (e is ApiException || !TresorOffline.decryptTo(context, item.path, dest)) throw e }
    }

    fun downloadServer(item: FileItem, dest: File) {
        if (isChat(item.path)) { val a = api(); val (m, i) = chatRef(item.path); a.download(a.url("/api/chat/messages/$m/refs/$i/file"), dest); return }
        if (isAlbum(item.path)) { val a = api(); a.download(a.url("/api/gallery/albums/${albumId(item.path)}/file", "key" to albumKey(item.path)), dest); return }
        if (isExt(item.path)) { val a = api(); a.download(a.url("/api/gallery/external/file", "id" to item.path.removePrefix(EXT)), dest); return }
        tooOld { api().downloadFile(item.path, dest, big = item.size > ENVELOPE_MAX, inline = isShown(item.path)) }
    }

    /**
     * Lokale Datei als [name] in den Ordner [dir] hochladen (ersetzt eine gleichnamige Datei). In einem
     * Offline-Ordner kommt sie in die lokale Kopie und der Abgleich laedt sie hoch - so nur einmal ueber das Netz.
     */
    fun upload(dir: String, name: String, mime: String?, file: File) {
        if (offline.ready(dir)) {
            val tmp = File.createTempFile("upload", ".tmp", File(context.cacheDir, "upload").apply { mkdirs() })
            file.copyTo(tmp, overwrite = true)
            offline.writeLocal(join(dir, name), tmp)
            afterChange(dir)
        } else uploadServer(dir, name, mime, file)
        invalidate(dir)
    }

    /** [onProgress]: gesendete Bytes - nur bei großen Dateien (in Stücken). */
    fun uploadServer(dir: String, name: String, mime: String?, file: File, onProgress: (Long) -> Unit = {}) {
        val a = api()
        val type = mime ?: guessMime(name)
        if (file.length() <= ENVELOPE_MAX) a.uploadFile(dir, name, type, file, big = false)
        else tooOld {
            // Gross: in Stuecken (je Teil ein Ticket), ohne Groessengrenze - am Server vor 2.0 in einem Stueck
            ChunkedUpload.orSingle(a, file.length(), context.getString(at.dimes.dmoffice.R.string.files_err_chunks_old), chunked = {
                val up = ChunkedUpload.upload(a, { file.inputStream() }, name, file.length(), type, onProgress = onProgress)
                ChunkedUpload.finishToFiles(a, up.id, dir)
            }, single = { a.uploadFile(dir, name, type, file, big = true) })
        }
        invalidate(dir)
    }

    /** Inhalt einer content://-Uri (Dateiauswahl, Kamera) hochladen; liefert den Namen am Ziel. */
    fun uploadUri(dir: String, uri: Uri, fallbackName: String? = null): String {
        val cr = context.contentResolver
        var name = fallbackName
        cr.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c -> if (c.moveToFirst() && !c.isNull(0)) name = c.getString(0) }
        val target = uniqueName(listCached(dir, 5_000), name?.takeIf { it.isNotBlank() } ?: "upload")
        val tmp = File.createTempFile("upload", ".tmp", File(context.cacheDir, "upload").apply { mkdirs() })
        try {
            cr.openInputStream(uri)?.use { i -> tmp.outputStream().use { o -> i.copyTo(o) } } ?: throw java.io.FileNotFoundException(uri.toString())
            upload(dir, target, cr.getType(uri), tmp)
        } finally { tmp.delete() }
        return target
    }

    // Aenderungen gehen zuerst an den Server; in Offline-Ordnern zieht der Abgleich danach die lokale Kopie nach

    fun mkdir(path: String) {
        mkdirServer(path)
        afterChange(path)
    }

    fun delete(path: String) {
        deleteServer(path)
        forgetMeta(path)
        // Markierte Ordner darunter (oder der Ordner selbst) sind weg
        if (offline.state.value.roots.keys.any { Offline.inside(it, path) }) offline.drop(path)
        afterChange(path)
    }

    fun move(from: String, to: String, copy: Boolean = false) {
        val a = api()
        a.postText(a.url("/api/files/move"), a.json.encodeToString(MoveBody.serializer(), MoveBody(from, to, copy)))
        invalidate(parent(from), parent(to), from)
        // Markierter Ordner umbenannt/verschoben: Markierung und lokale Kopie wandern mit
        if (!copy && offline.state.value.roots.keys.any { Offline.inside(it, from) }) offline.moved(from, to)
        if (!copy) afterChange(from)
        afterChange(to)
    }

    fun mkdirServer(path: String) {
        val a = api()
        a.post(a.url("/api/files/mkdir"), mapOf("path" to path))
        invalidate(parent(path))
    }

    fun deleteServer(path: String) {
        val a = api()
        a.post(a.url("/api/files/delete"), mapOf("path" to path))
        invalidate(parent(path), path)
    }

    /** Nach einer Server-Aenderung: den betroffenen Offline-Ordner gleich abgleichen (Fehler holt der Worker nach). */
    private fun afterChange(path: String) {
        if (offline.rootOf(path) == null) return
        try { offline.sync(path) } catch (e: Exception) { OfflineWorker.now(context) }
    }

    /** Alles Zwischengespeicherte weg (nach "Konto entfernen"). */
    fun clear() {
        listings.clear()
        File(context.cacheDir, "files").deleteRecursively()
        File(context.cacheDir, "docs").deleteRecursively()
        File(context.cacheDir, "upload").deleteRecursively()
        File(context.cacheDir, "camera").deleteRecursively()
        File(context.cacheDir, "offline-tmp").deleteRecursively()
        Thumbs.clear(context)
        offline.clear()
    }

    @Serializable private data class MoveBody(val from: String, val to: String, val copy: Boolean, val conflict: String? = null)
    @Serializable private data class MoveResult(val path: String? = null, val skipped: Boolean = false)
    @Serializable private data class FavBody(val path: String, val on: Boolean)
    @Serializable private data class PathsBody(val paths: List<String>)
    @Serializable private data class TrashIds(val trash: List<Long> = emptyList())
    @Serializable private data class IdsBody(val ids: List<Long>)
    @Serializable private data class VersionBody(val path: String, val id: Long)
    @Serializable private data class LinkBody(val path: String?, val mode: String, val expires: String?, val password: String?, val maxDownloads: Int)

    companion object {
        /**
         * Bis zu dieser Groesse gehen Dateiinhalte im verschluesselten Umschlag; groessere direkt ueber TLS
         * (wie die Download-Links im Web), weil der Umschlag den ganzen Inhalt mehrfach im Speicher haelt
         * und die App bei einigen zehn MB abstuerzt.
         */
        const val ENVELOPE_MAX = 8L * 1024 * 1024

        /** System-Tresor in der Dateiansicht (docs/tresor-cryptomator-konzept.md, 3a): Pfade „tresor:“ und „tresor:/A/B“. */
        const val TRESOR = "tresor:"
        fun isTresor(path: String) = path == TRESOR || path.startsWith("$TRESOR/")

        fun parent(path: String): String = path.substringBeforeLast('/', "")
        fun join(dir: String, name: String): String = if (dir.isEmpty()) name else "$dir/$name"
        fun nameOf(path: String): String = path.substringAfterLast('/')

        fun guessMime(name: String): String =
            MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "application/octet-stream"

        /** Freier Name im Ordner: "Bild.jpg" -> "Bild (1).jpg", wenn es "Bild.jpg" schon gibt. */
        fun uniqueName(l: Listing, name: String): String {
            val taken = l.items.map { it.name.lowercase() }.toSet()
            if (name.lowercase() !in taken) return name
            val dot = name.lastIndexOf('.').takeIf { it > 0 } ?: name.length
            val base = name.substring(0, dot); val ext = name.substring(dot)
            var i = 1
            while ("$base ($i)$ext".lowercase() in taken) i++
            return "$base ($i)$ext"
        }

        val RAW_EXT = setOf("arw", "cr2", "cr3", "nef", "nrw", "orf", "rw2", "raf", "pef", "dng", "srw", "3fr", "erf")
        /** „Mit mir geteilt“ in der Wurzel: was andere teilen - teilen kann das nur der Besitzer */
        const val WITH_ME = "Mit mir geteilt"
        const val ALB = "alb:"
        fun isAlbum(path: String) = path.startsWith(ALB)
        /** Foto aus einem Galerie-Ordner: „ext:Quelle/Pfad“ */
        /** Foto aus dem Chat im Bildbetrachter: „chat:Nachricht:Index“ (Bilder über die Adressen des Chats). */
        const val CHAT = "chat:"
        fun isChat(path: String) = path.startsWith(CHAT)
        fun chatRef(path: String): Pair<Long, Int> = path.removePrefix(CHAT).split(':').let { it[0].toLong() to it[1].toInt() }
        fun chatItem(message: Long, index: Int, name: String, mime: String?, size: Long?) = FileItem("$CHAT$message:$index", name, false, size ?: 0, mime)
        const val EXT = "ext:"
        fun isExt(path: String) = path.startsWith(EXT)
        fun albumId(path: String) = path.removePrefix(ALB).substringBefore('/')
        fun albumKey(path: String) = path.removePrefix(ALB).substringAfter('/')
        fun safeName(name: String): String = name.ifBlank { "datei" }.replace(Regex("[\\\\/:*?\"<>|\\u0000-\\u001f]"), "_")

        private fun hash(s: String): String = MessageDigest.getInstance("SHA-1").digest(s.toByteArray()).joinToString("") { "%02x".format(it) }.take(16)
    }
}
