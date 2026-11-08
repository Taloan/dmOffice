package at.dimes.dmoffice.ui

import android.content.Context
import android.media.MediaPlayer
import android.media.MediaRecorder
import android.os.Build
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.chat.ChatMessage
import at.dimes.dmoffice.chat.ChatRef
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.io.File

/** Laengste Sprachnachricht: 5 Minuten, dann wird sie von selbst geschickt (wie im Web). */
const val MaxVoiceMs = 5 * 60_000L

/** Dauer als m:ss */
fun voiceTime(ms: Long): String { val s = (ms / 1000).coerceAtLeast(0); return "${s / 60}:${(s % 60).toString().padStart(2, '0')}" }

/** Sprachnachricht aufnehmen (AAC in m4a) - stop liefert die Datei, null wenn sie zu kurz war. */
class VoiceRecorder(context: Context, private val file: File) {
    private val rec: MediaRecorder = (if (Build.VERSION.SDK_INT >= 31) MediaRecorder(context) else @Suppress("DEPRECATION") MediaRecorder()).apply {
        setAudioSource(MediaRecorder.AudioSource.MIC)
        setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
        setAudioEncoder(MediaRecorder.AudioEncoder.AAC)
        setAudioEncodingBitRate(64_000)
        setAudioSamplingRate(44_100)
        setOutputFile(file.absolutePath)
        prepare()
        start()
    }

    fun stop(): File? {
        val ok = try { rec.stop(); true } catch (_: RuntimeException) { false }   // zu kurz: nichts aufgenommen
        rec.release()
        if (!ok || file.length() == 0L) { file.delete(); return null }
        return file
    }

    fun cancel() { try { rec.stop() } catch (_: RuntimeException) { }; rec.release(); file.delete() }
}

/** Es spielt immer nur eine Sprachnachricht - die naechste haelt die vorige an. */
private object VoicePlayback {
    var player: MediaPlayer? = null
    var key: String? = null
    fun stop() { try { player?.release() } catch (_: Exception) { }; player = null; key = null }
}

/** Ton im Verlauf: Abspielen/Anhalten, Fortschritt, Dauer. Die Datei kommt beim ersten Abspielen in den Zwischenspeicher. */
@Composable
fun VoicePlayer(m: ChatMessage, index: Int, ref: ChatRef) {
    val app = App.get(LocalContext.current)
    val scope = rememberCoroutineScope()
    val key = "${m.id}/$index"
    var loading by remember { mutableStateOf(false) }
    var playing by remember { mutableStateOf(false) }
    var position by remember { mutableStateOf(0L) }
    var duration by remember { mutableStateOf(0L) }
    LaunchedEffect(playing) {
        while (playing) {
            val p = VoicePlayback.player
            if (VoicePlayback.key != key || p == null) { playing = false; break }
            position = try { p.currentPosition.toLong() } catch (_: Exception) { 0L }
            delay(200)
        }
    }
    DisposableEffect(key) { onDispose { if (VoicePlayback.key == key) VoicePlayback.stop() } }

    fun toggle() {
        val p = VoicePlayback.player
        if (VoicePlayback.key == key && p != null) {
            if (p.isPlaying) { p.pause(); playing = false } else { p.start(); playing = true }
            return
        }
        scope.launch {
            loading = true
            try {
                val f = app.chat.download(m.id, index, ref.name)
                VoicePlayback.stop()
                val player = MediaPlayer().apply {
                    setDataSource(f.absolutePath)
                    prepare()
                    setOnCompletionListener { playing = false; position = 0L; VoicePlayback.stop() }
                }
                duration = player.duration.toLong()
                VoicePlayback.player = player
                VoicePlayback.key = key
                player.start()
                playing = true
            } catch (_: Exception) { playing = false } finally { loading = false }
        }
    }

    Row(Modifier.padding(vertical = 2.dp).clip(RoundedCornerShape(20.dp)).background(MaterialTheme.colorScheme.surfaceVariant).padding(end = 12.dp),
        verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = { toggle() }) {
            if (loading) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
            else Icon(if (playing) Icons.Default.Pause else Icons.Default.PlayArrow,
                stringResource(if (playing) R.string.chat_voice_pause else R.string.chat_voice_play))
        }
        LinearProgressIndicator(progress = { if (duration > 0) (position.toFloat() / duration).coerceIn(0f, 1f) else 0f }, Modifier.width(130.dp))
        Text(voiceTime(if (playing || position > 0) position else duration), Modifier.padding(start = 10.dp),
            style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** Roter Punkt der laufenden Aufnahme */
@Composable
fun RecordingDot() {
    androidx.compose.foundation.layout.Box(Modifier.size(10.dp).clip(CircleShape).background(MaterialTheme.colorScheme.error))
}
