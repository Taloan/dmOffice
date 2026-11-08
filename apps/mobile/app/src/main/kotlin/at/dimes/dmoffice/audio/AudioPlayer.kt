package at.dimes.dmoffice.audio

import android.content.ComponentName
import android.content.Context
import androidx.core.content.ContextCompat
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import kotlinx.coroutines.flow.MutableStateFlow

/**
 * Steuerung der Wiedergabe aus der Oberfläche: eine MediaController-Verbindung zu [AudioService]. Der Stand ([now], [queue])
 * kommt als StateFlow; Befehle vor dem Verbinden warten, bis die Verbindung steht.
 */
object AudioPlayer {
    data class Now(
        val id: String? = null, val title: String = "", val artist: String = "", val album: String = "", val cover: String? = null,
        val playing: Boolean = false, val duration: Long = 0, val index: Int = -1, val count: Int = 0,
        val shuffle: Boolean = false, val repeat: Int = Player.REPEAT_MODE_OFF,
    )

    val now = MutableStateFlow(Now())
    val queue = MutableStateFlow<List<MediaItem>>(emptyList())

    private var controller: MediaController? = null
    private var connecting = false
    private val waiting = mutableListOf<(MediaController) -> Unit>()

    private val listener = object : Player.Listener {
        override fun onEvents(player: Player, events: Player.Events) { update(player) }
    }

    /** Verbinden (beim Öffnen der App) - zeigt eine laufende Wiedergabe sofort an. */
    fun connect(ctx: Context) {
        if (controller != null || connecting) return
        connecting = true
        val app = ctx.applicationContext
        val f = MediaController.Builder(app, SessionToken(app, ComponentName(app, AudioService::class.java))).buildAsync()
        f.addListener({
            connecting = false
            val c = try { f.get() } catch (_: Exception) { return@addListener }
            controller = c
            c.addListener(listener)
            update(c)
            waiting.toList().forEach { it(c) }
            waiting.clear()
        }, ContextCompat.getMainExecutor(app))
    }

    private fun with(ctx: Context, f: (MediaController) -> Unit) {
        controller?.let { f(it); return }
        waiting += f
        connect(ctx)
    }

    private fun update(p: Player) {
        val item = p.currentMediaItem
        val md = item?.mediaMetadata
        now.value = Now(item?.mediaId, md?.title?.toString() ?: "", md?.artist?.toString() ?: "", md?.albumTitle?.toString() ?: "",
            md?.extras?.getString("cover"), p.isPlaying, p.duration.coerceAtLeast(0), p.currentMediaItemIndex, p.mediaItemCount,
            p.shuffleModeEnabled, p.repeatMode)
        queue.value = (0 until p.mediaItemCount).map { p.getMediaItemAt(it) }
    }

    fun item(t: Track): MediaItem = MediaItem.Builder()
        .setMediaId(t.id)
        .setUri(AudioService.uriOf(t.id))
        .setMediaMetadata(MediaMetadata.Builder()
            .setTitle(t.title).setArtist(t.artistShown).setAlbumTitle(t.album).setAlbumArtist(t.albumArtist).setGenre(t.genre)
            .setTrackNumber(t.track).setDiscNumber(t.disc).setReleaseYear(t.year)
            .setDurationMs(t.duration?.let { (it * 1000).toLong() })
            .setArtworkUri(t.cover?.let { AudioService.coverUri(it) })
            .setExtras(android.os.Bundle().apply { putString("cover", t.cover) })
            .setIsPlayable(true).setIsBrowsable(false)
            .build())
        .build()

    /** Liste ab einem Titel abspielen; [shuffle]: gemischt (Zufall an). */
    fun play(ctx: Context, tracks: List<Track>, start: Int = 0, shuffle: Boolean? = null) {
        if (tracks.isEmpty()) return
        with(ctx) { c ->
            if (shuffle != null) c.shuffleModeEnabled = shuffle
            val first = if (shuffle == true) tracks.indices.random() else start.coerceIn(tracks.indices)
            c.setMediaItems(tracks.map(::item), first, 0)
            c.prepare()
            c.play()
        }
    }

    fun toggle(ctx: Context) = with(ctx) { c -> if (c.isPlaying) c.pause() else { if (c.playbackState == Player.STATE_IDLE) c.prepare(); c.play() } }
    fun next(ctx: Context) = with(ctx) { it.seekToNextMediaItem() }
    fun prev(ctx: Context) = with(ctx) { it.seekToPrevious() }
    fun seek(ctx: Context, ms: Long) = with(ctx) { it.seekTo(ms) }
    fun jump(ctx: Context, index: Int) = with(ctx) { it.seekTo(index, 0); it.play() }
    fun remove(ctx: Context, index: Int) = with(ctx) { it.removeMediaItem(index) }
    fun setShuffle(ctx: Context, on: Boolean) = with(ctx) { it.shuffleModeEnabled = on }
    fun cycleRepeat(ctx: Context) = with(ctx) {
        it.repeatMode = when (it.repeatMode) { Player.REPEAT_MODE_OFF -> Player.REPEAT_MODE_ALL; Player.REPEAT_MODE_ALL -> Player.REPEAT_MODE_ONE; else -> Player.REPEAT_MODE_OFF }
    }
    fun stop(ctx: Context) = with(ctx) { it.stop(); it.clearMediaItems() }

    /** Nach dem Verbinden (bzw. gleich): wie viele Titel in der Warteschlange stehen. */
    fun afterConnect(ctx: Context, f: (Int) -> Unit) = with(ctx) { f(it.mediaItemCount) }

    /** Aktuelle Position (ms) - zum Abfragen im Takt, solange die Anzeige offen ist. */
    fun position(): Long = controller?.currentPosition ?: 0
}
