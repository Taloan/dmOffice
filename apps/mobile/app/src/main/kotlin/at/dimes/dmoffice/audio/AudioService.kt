package at.dimes.dmoffice.audio

import android.app.PendingIntent
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.SystemClock
import androidx.annotation.OptIn
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.common.util.BitmapLoader
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.datasource.ResolvingDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import at.dimes.dmoffice.App
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.widget.AudioWidgetState
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.MoreExecutors
import java.io.IOException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors

/**
 * Wiedergabe der Audiothek (Version 2): ExoPlayer in einer MediaSession - spielt im Hintergrund weiter, mit Benachrichtigung,
 * Sperrbildschirm und Bluetooth-Tasten. Titel kommen als „dmaudio://t?id=…“; erst beim Laden wird daraus die Adresse am
 * Server mit Ticket (Umschlag) bzw. App-Passwort ([at.dimes.dmoffice.api.ApiClient.streamRequest]). Wie lange ein Titel lief,
 * meldet der Dienst beim Wechsel an den Server ([AudioRepo.reportBlocking], ohne Netz später).
 */
@OptIn(UnstableApi::class)
class AudioService : MediaSessionService() {
    private var session: MediaSession? = null
    private lateinit var player: ExoPlayer
    private val app get() = App.get(this)

    // Hörverlauf: Spielzeit des aktuellen Titels (nur, während er wirklich läuft)
    private var currentId: String? = null
    private var listened = 0L
    private var since = 0L

    private val listener = object : Player.Listener {
        override fun onIsPlayingChanged(isPlaying: Boolean) {
            val now = SystemClock.elapsedRealtime()
            if (isPlaying) since = now else if (since > 0) { listened += now - since; since = 0 }
        }
        // Widget auf dem Startbildschirm nachführen
        override fun onEvents(player: Player, events: Player.Events) { widget() }
        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            finish()
            currentId = mediaItem?.mediaId
            if (player.isPlaying) since = SystemClock.elapsedRealtime()
        }
    }

    private fun widget() {
        val item = player.currentMediaItem
        if (item == null) { AudioWidgetState.clear(this); return }
        val md = item.mediaMetadata
        AudioWidgetState.set(this, AudioWidgetState.S(item.mediaId, md.title?.toString() ?: "", md.artist?.toString() ?: "", md.extras?.getString("cover"),
            player.playWhenReady && player.playbackState != Player.STATE_ENDED, player.hasNextMediaItem()))
    }

    /** Spielzeit des laufenden Titels melden und von vorn zählen. */
    private fun finish() {
        val now = SystemClock.elapsedRealtime()
        val ms = listened + (if (since > 0) now - since else 0)
        val id = currentId
        listened = 0; since = if (since > 0) now else 0
        if (id != null && ms >= 1000) Thread { app.audio.reportBlocking(id, ms) }.start()
    }

    override fun onCreate() {
        super.onCreate()
        // Lese-Tickets gelten 4 Stunden je Adresse; beim Spulen öffnet der Player die Adresse neu - nicht jedes Mal ein neues holen
        val tickets = ConcurrentHashMap<String, Pair<Pair<String, Map<String, String>>, Long>>()
        val resolver = ResolvingDataSource.Resolver { spec ->
            val id = spec.uri.takeIf { it.scheme == SCHEME }?.getQueryParameter("id") ?: return@Resolver spec
            val api = app.api ?: throw IOException("Kein Konto")
            val url = api.url("/api/audio/stream", "id" to id)
            val now = SystemClock.elapsedRealtime()
            val (target, headers) = tickets[url]?.takeIf { now - it.second < 3 * 3600_000L }?.first
                ?: api.streamRequest(url).also { tickets[url] = it to now }
            spec.withUri(Uri.parse(target)).withAdditionalHeaders(headers)
        }
        val http = DefaultHttpDataSource.Factory().setUserAgent("dmOffice-Android/0.1").setConnectTimeoutMs(20_000).setReadTimeoutMs(60_000)
        player = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(ResolvingDataSource.Factory(http, resolver)))
            .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MUSIC).build(), true)
            .setHandleAudioBecomingNoisy(true)
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build()
        player.addListener(listener)
        val open = PendingIntent.getActivity(this, 7301,
            Intent(this, MainActivity::class.java).putExtra(MainActivity.EXTRA_TAB, "audio").addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        session = MediaSession.Builder(this, player)
            .setSessionActivity(open)
            .setBitmapLoader(CoverLoader(app.audio))
            .setCallback(object : MediaSession.Callback {
                // Über die Steuerung kommen nur Kennung und Angaben an - die Adresse zum Laden setzt der Dienst selbst
                override fun onAddMediaItems(mediaSession: MediaSession, controller: MediaSession.ControllerInfo, mediaItems: MutableList<MediaItem>): ListenableFuture<MutableList<MediaItem>> =
                    Futures.immediateFuture(mediaItems.map { it.buildUpon().setUri(uriOf(it.mediaId)).build() }.toMutableList())
            })
            .build()
        Thread { try { app.audio.flushBlocking() } catch (_: Exception) { } }.start()
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? = session

    /** App weggewischt: weiterspielen, wenn gerade etwas läuft, sonst beenden. */
    override fun onTaskRemoved(rootIntent: Intent?) {
        if (!player.playWhenReady || player.mediaItemCount == 0) stopSelf()
    }

    override fun onDestroy() {
        finish()
        // Warteschlange weg: das Widget bietet wieder „Audiothek öffnen“ an
        AudioWidgetState.clear(this)
        session?.run { player.release(); release() }
        session = null
        super.onDestroy()
    }

    /** Cover für Benachrichtigung und Sperrbildschirm („dmcover:Kennung“) über die App - mit Anmeldung, aus dem Cache. */
    private class CoverLoader(private val repo: AudioRepo) : BitmapLoader {
        private val exec = MoreExecutors.listeningDecorator(Executors.newSingleThreadExecutor())
        override fun supportsMimeType(mimeType: String) = mimeType.startsWith("image/")
        override fun decodeBitmap(data: ByteArray): ListenableFuture<Bitmap> = exec.submit<Bitmap> {
            BitmapFactory.decodeByteArray(data, 0, data.size) ?: throw IOException("Kein Bild")
        }
        override fun loadBitmap(uri: Uri): ListenableFuture<Bitmap> = exec.submit<Bitmap> {
            if (uri.scheme != COVER_SCHEME) throw IOException("Unbekannte Adresse")
            repo.coverBlocking(uri.schemeSpecificPart) ?: throw IOException("Kein Cover")
        }
    }

    companion object {
        const val SCHEME = "dmaudio"
        const val COVER_SCHEME = "dmcover"
        fun uriOf(id: String): Uri = Uri.parse("$SCHEME://t?id=" + Uri.encode(id))
        fun coverUri(id: String): Uri = Uri.parse("$COVER_SCHEME:" + id)
    }
}
