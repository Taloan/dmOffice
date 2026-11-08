package at.dimes.dmoffice.audio

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import java.io.File
import java.io.IOException

/** Ein Titel der Audiothek - wie im Web (GET /api/audio/tracks). */
data class Track(
    val id: String, val title: String, val artist: String?, val albumArtist: String?, val album: String?,
    val track: Int?, val disc: Int?, val year: Int?, val genre: String?, val duration: Double?, val cover: String?, val modified: Long,
) {
    val artistShown: String get() = artist ?: albumArtist ?: ""
    /** Kleingeschrieben: alles, was die Suche findet */
    val text: String by lazy { listOfNotNull(title, artist, albumArtist, album, genre, year?.toString()).joinToString(" ").lowercase() }
}

/** Ein Album: Titel nach Album und Album-Interpret, ohne Album-Interpret nach Album und Ordner (wie im Web). */
data class Album(val key: String, val name: String, val artist: String, val year: Int?, val cover: String?, val tracks: List<Track>)

/** Interpret oder Genre: seine Titel (Bibliotheksreihenfolge) und Alben; [key] = Name klein. */
data class Group(val key: String, val name: String, val tracks: List<Track>, val albums: List<Album>) {
    /** Bis zu vier verschiedene Albumcover (Mosaik) */
    val covers: List<String> get() = albums.mapNotNull { it.cover }.distinct().take(4)
}

@Serializable data class PlaylistInfo(val id: String, val name: String, val count: Int = 0, val modified: Long = 0, val cover: String? = null)
@Serializable private data class PlaylistList(val playlists: List<PlaylistInfo> = emptyList(), val auto: List<String> = emptyList())
@Serializable private data class TrackIds(val tracks: List<String> = emptyList())
@Serializable private data class Played(val id: String, val ms: Long, val device: String, val at: String? = null)
@Serializable private data class PendingPlay(val id: String, val ms: Long, val at: String)

/**
 * Audiothek (Version 2, docs/audiothek-konzept.md Stufe 3): Titelliste mit ETag (auf dem Gerät gemerkt, damit die Ansicht
 * sofort steht), Playlists und Auto-Playlists vom Server, Cover, Hörverlauf. Gehörtes ohne Netz wartet und wird nachgemeldet.
 * Bearbeitet werden Playlists im Web.
 */
class AudioRepo(private val app: App) {
    private fun api(): ApiClient = app.api ?: throw IllegalStateException("Kein Konto")
    private val sp get() = app.getSharedPreferences("audio", Context.MODE_PRIVATE)

    /** Audiothek für dieses Konto da (Anmeldestand user.audio); zuletzt bekannter Stand, bis der Server antwortet. */
    val enabled = MutableStateFlow(sp.getBoolean("enabled", false))
    fun setEnabled(on: Boolean) {
        val changed = on != enabled.value || !sp.contains("enabled")
        enabled.value = on
        sp.edit().putBoolean("enabled", on).apply()
        // Widget des Startbildschirms nur anbieten, wenn es die Audiothek gibt
        if (changed) try {
            app.packageManager.setComponentEnabledSetting(android.content.ComponentName(app, at.dimes.dmoffice.widget.AudioWidgetReceiver::class.java),
                if (on) android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_DEFAULT else android.content.pm.PackageManager.COMPONENT_ENABLED_STATE_DISABLED,
                android.content.pm.PackageManager.DONT_KILL_APP)
        } catch (_: Exception) { }
    }

    /**
     * Beim Server nachfragen: Die Schnittstelle antwortet 404, wenn die Audiothek nicht freigeschaltet, ausgeschaltet oder für
     * dieses Konto nicht da ist (ohne Dateiablage). Ohne Netz bleibt der letzte Stand. Lädt nebenbei die Playlists.
     */
    suspend fun checkEnabled() {
        try { refreshPlaylists(); setEnabled(true) }
        catch (e: ApiException) { if (e.code == 404 || e.code == 401 || e.code == 403) setEnabled(false) }
        catch (_: Exception) { }
    }

    val tracks = MutableStateFlow<List<Track>?>(null)
    /** Je Konto (Server und Anmeldename) eigene Liste auf dem Gerät */
    private val acct get() = (app.prefs.account.value?.let { it.baseUrl + "|" + it.login } ?: "").hashCode().toUInt().toString(16)
    private val cacheFile get() = File(app.filesDir, "audio-tracks-$acct.json")

    private suspend fun <T> io(f: () -> T): T = withContext(Dispatchers.IO) { f() }

    /** Titelliste: erst vom Gerät, dann vom Server (unverändert: 304). */
    suspend fun refresh(): List<Track> = io {
        if (tracks.value == null) try { cacheFile.takeIf { it.isFile }?.readText()?.let { tracks.value = decode(it) } } catch (_: Exception) { }
        val a = api()
        val etag = sp.getString("etag-$acct", null)?.takeIf { tracks.value != null }
        val list = when (val r = a.getIfChanged(a.url("/api/audio/tracks"), etag)) {
            null -> tracks.value!!
            else -> decode(r.first).also { cacheFile.writeText(r.first); sp.edit().putString("etag-$acct", r.second).apply() }
        }
        tracks.value = list
        list
    }

    private fun decode(text: String): List<Track> {
        val o = api().json.parseToJsonElement(text).jsonObject
        val fields = o["fields"]!!.jsonArray.map { it.jsonPrimitive.content }
        fun ix(n: String) = fields.indexOf(n)
        val id = ix("id"); val ti = ix("title"); val ar = ix("artist"); val aa = ix("albumArtist"); val al = ix("album"); val tr = ix("track")
        val di = ix("disc"); val ye = ix("year"); val ge = ix("genre"); val du = ix("duration"); val co = ix("cover"); val mo = ix("modified")
        return o["tracks"]!!.jsonArray.map { e ->
            val r = e as JsonArray
            fun s(i: Int) = if (i < 0) null else r[i].jsonPrimitive.contentOrNull
            val tid = s(id)!!
            Track(tid, s(ti) ?: tid.substringAfterLast('/'), s(ar), s(aa), s(al), r.getOrNull(tr)?.jsonPrimitive?.intOrNull, r.getOrNull(di)?.jsonPrimitive?.intOrNull,
                r.getOrNull(ye)?.jsonPrimitive?.intOrNull, s(ge), r.getOrNull(du)?.jsonPrimitive?.doubleOrNull, s(co), r.getOrNull(mo)?.jsonPrimitive?.longOrNull ?: 0)
        }
    }

    // ---- Playlists ----

    val playlists = MutableStateFlow<List<PlaylistInfo>>(emptyList())
    val autoKinds = MutableStateFlow<List<String>>(emptyList())

    suspend fun refreshPlaylists() = io {
        val a = api()
        val r: PlaylistList = a.get(a.url("/api/audio/playlists"))
        playlists.value = r.playlists
        autoKinds.value = r.auto
    }

    /** Titel einer Playlist; „auto:Art“ für Auto-Playlists. Nur Titel, die die Liste kennt. */
    suspend fun playlistTracks(key: String): List<Track> = io {
        val a = api()
        val ids: TrackIds = if (key.startsWith("auto:")) a.get(a.url("/api/audio/playlists/auto/" + key.removePrefix("auto:")))
            else a.get(a.url("/api/audio/playlists/" + key))
        val byId = (tracks.value ?: emptyList()).associateBy { it.id }
        ids.tracks.mapNotNull { byId[it] }
    }

    // ---- Abspielen ----

    /** Adresse zum Abspielen (Range); der Player holt sie mit Ticket bzw. App-Passwort (siehe [AudioService]). */
    fun streamUrl(id: String): String = api().url("/api/audio/stream", "id" to id)

    private val covers = object : LruCache<String, Bitmap>(16 * 1024 * 1024) { override fun sizeOf(key: String, value: Bitmap) = value.byteCount }
    private fun coverDir() = File(app.cacheDir, "audio-covers").apply { mkdirs() }

    /** Hat keines (404): in dieser Sitzung nicht wieder fragen. */
    private val missingCovers = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()
    /** Nicht zu viele Abrufe gleichzeitig (Kachelansicht mit vielen Alben) - wie bei den Vorschaubildern der Dateien. */
    private val coverGate = java.util.concurrent.Semaphore(4)

    fun cachedCover(id: String): Bitmap? = covers.get(id)

    /**
     * Cover (300 px) - aus dem Speicher, vom Gerät oder vom Server. Blockiert. Kennung = Prüfsumme, ändert sich nie.
     * Auf dem Gerät landet nur ein lesbares Bild; eine kaputte Datei (z. B. leer nach einem Abbruch) wird verworfen und neu geholt -
     * vorher blieb sie für immer liegen, und die Kachel zeigte nur die Note.
     */
    fun coverBlocking(id: String): Bitmap? {
        covers.get(id)?.let { return it }
        if (id in missingCovers) return null
        val f = File(coverDir(), id.filter { it.isLetterOrDigit() } + ".jpg")
        if (f.isFile) {
            val b = try { f.readBytes() } catch (_: Exception) { null }
            b?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }?.let { covers.put(id, it); return it }
            f.delete()
        }
        coverGate.acquireUninterruptibly()
        try {
            covers.get(id)?.let { return it }
            val bytes = try { val a = api(); a.getBytes(a.url("/api/audio/cover/" + java.net.URLEncoder.encode(id, "UTF-8"))).first }
                catch (e: ApiException) { if (e.code == 404) missingCovers += id; return null }
                catch (_: Exception) { return null }
            val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size) ?: return null
            try { f.writeBytes(bytes) } catch (_: Exception) { }
            covers.put(id, bmp)
            return bmp
        } finally { coverGate.release() }
    }

    suspend fun cover(id: String): Bitmap? = io { coverBlocking(id) }

    // ---- Hörverlauf ----

    /** Gespielt: an den Server; ohne Netz merken und später nachmelden (mit dem Zeitpunkt von damals). Blockiert. */
    fun reportBlocking(id: String, ms: Long) {
        if (ms < 1000) return
        val at = java.time.Instant.now().toString()
        try { flushBlocking(); send(id, ms, at) }
        catch (e: IOException) { if (e !is ApiException || e.code >= 500) remember(PendingPlay(id, ms, at)) }
        catch (_: Exception) { }
    }

    private fun send(id: String, ms: Long, at: String?) {
        val a = api()
        a.post(a.url("/api/audio/played"), Played(id, ms, "App", at))
    }

    @Synchronized private fun remember(p: PendingPlay) {
        val list = pending() + p
        sp.edit().putString("pending", api().json.encodeToString(list.takeLast(2000))).apply()
    }

    private fun pending(): List<PendingPlay> = try { api().json.decodeFromString(sp.getString("pending", null) ?: "[]") } catch (_: Exception) { emptyList() }

    /** Gemerktes nachmelden; bricht beim ersten Netzfehler ab. Verworfenes (Titel weg: 404) fällt heraus. */
    @Synchronized fun flushBlocking() {
        var list = pending()
        while (list.isNotEmpty()) {
            val p = list.first()
            try { send(p.id, p.ms, p.at) } catch (e: ApiException) { if (e.code >= 500) break }
            list = list.drop(1)
            sp.edit().putString("pending", api().json.encodeToString(list)).apply()
        }
    }

    companion object {
        /** Alben wie im Web (library.ts albumsOf) */
        fun albums(tracks: List<Track>, various: String, unknown: String): List<Album> =
            tracks.groupBy { albumKey(it) }.map { (key, list) ->
                val sorted = list.sortedWith(compareBy<Track>({ it.disc ?: 1 }, { it.track ?: 9999 }, { it.id }))
                val artists = sorted.mapNotNull { it.albumArtist ?: it.artist }.toSet()
                val first = sorted.first()
                Album(key, first.album ?: unknown, first.albumArtist ?: if (artists.size == 1) artists.first() else if (artists.size > 1) various else "",
                    sorted.firstNotNullOfOrNull { it.year }, sorted.firstNotNullOfOrNull { it.cover }, sorted)
            }.sortedWith(compareBy<Album>({ it.artist.lowercase() }, { it.year ?: 0 }, { it.name.lowercase() }))

        /** Schlüssel des Albums eines Titels: Album und Album-Interpret, ohne Album-Interpret Album und Ordner (wie [albums]) */
        private val discFolder = Regex("/(cd|disc|disk)\\s*\\d+$", RegexOption.IGNORE_CASE)
        fun albumKey(t: Track): String {
            val folder = t.id.substringBeforeLast('/').replace(discFolder, "").lowercase()
            return (t.album ?: "").lowercase() + "|" + (t.albumArtist?.lowercase() ?: folder)
        }

        /** Interpreten wie im Web (library.ts artistsOf): ein Titel zählt bei Interpret und Album-Interpret; Schlüssel = Name klein. */
        fun artists(tracks: List<Track>, various: String, unknownAlbum: String, unknownArtist: String): List<Group> {
            val map = LinkedHashMap<String, Pair<String, MutableList<Track>>>()
            for (t in tracks) {
                val names = listOfNotNull(t.albumArtist, t.artist).distinctBy { it.lowercase() }.ifEmpty { listOf(unknownArtist) }
                for (n in names) map.getOrPut(n.lowercase()) { n to mutableListOf() }.second += t
            }
            return map.map { (k, v) -> Group(k, v.first, v.second, albums(v.second, various, unknownAlbum)) }.sortedBy { it.name.lowercase() }
        }

        /** Genres wie im Web (library.ts genresList): mehrere mit „;“ getrennt; die Alben nur für die Cover. */
        fun genres(tracks: List<Track>, various: String, unknownAlbum: String, noGenre: String): List<Group> {
            val map = LinkedHashMap<String, Pair<String, MutableList<Track>>>()
            for (t in tracks) for (g in genresOf(t).ifEmpty { listOf(noGenre) }) map.getOrPut(g.lowercase()) { g to mutableListOf() }.second += t
            return map.map { (k, v) -> Group(k, v.first, v.second, albums(v.second, various, unknownAlbum)) }.sortedBy { it.name.lowercase() }
        }

        fun genresOf(t: Track): List<String> = (t.genre ?: "").split(';').map { it.trim() }.filter { it.isNotEmpty() }.distinctBy { it.lowercase() }

        /** Bibliotheksreihenfolge: Interpret, Album, CD, Titelnummer */
        val byLibrary: Comparator<Track> = compareBy<Track>({ (it.albumArtist ?: it.artist ?: "").lowercase() }, { (it.album ?: "").lowercase() },
            { it.disc ?: 1 }, { it.track ?: 9999 }, { it.id })

        fun search(tracks: List<Track>, query: String): List<Track> {
            val words = query.lowercase().split(Regex("\\s+")).filter { it.isNotEmpty() }
            return if (words.isEmpty()) tracks else tracks.filter { t -> words.all { t.text.contains(it) } }
        }
    }
}
