package at.dimes.dmoffice.widget

import android.content.Context
import android.content.Intent
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.ColorFilter
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.LocalSize
import androidx.glance.action.ActionParameters
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.appwidget.updateAll
import androidx.glance.background
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxHeight
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextStyle
import at.dimes.dmoffice.App
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.R
import at.dimes.dmoffice.audio.AudioPlayer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Stand für das Widget der Audiothek: laufender Titel, ob er spielt, ob es weitergeht. Schreibt der Wiedergabedienst
 * ([at.dimes.dmoffice.audio.AudioService]) bei jeder Änderung; ohne Warteschlange (Dienst beendet) ist [id] null.
 */
object AudioWidgetState {
    data class S(val id: String?, val title: String, val artist: String, val cover: String?, val playing: Boolean, val hasNext: Boolean)

    private fun sp(ctx: Context) = ctx.getSharedPreferences("audio-widget", Context.MODE_PRIVATE)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    @Volatile private var last: S? = null

    fun get(ctx: Context): S = sp(ctx).let { p ->
        S(p.getString("id", null), p.getString("title", "") ?: "", p.getString("artist", "") ?: "", p.getString("cover", null), p.getBoolean("playing", false), p.getBoolean("next", false))
    }

    /** Neuer Stand; das Widget wird nur neu gezeichnet, wenn sich etwas Sichtbares geändert hat. */
    fun set(ctx: Context, s: S) {
        if (s == last) return
        last = s
        sp(ctx).edit().putString("id", s.id).putString("title", s.title).putString("artist", s.artist).putString("cover", s.cover)
            .putBoolean("playing", s.playing).putBoolean("next", s.hasNext).apply()
        val app = ctx.applicationContext
        scope.launch { try { AudioWidget().updateAll(app) } catch (_: Exception) { } }
    }

    fun clear(ctx: Context) = set(ctx, S(null, "", "", null, false, false))
}

/**
 * Player der Audiothek für den Startbildschirm (und Sperrbildschirm, wo erlaubt): Cover, Titel und Interpret, Zurück,
 * Abspielen/Pause, Weiter. Tippen auf Cover oder Titel öffnet die Audiothek der App. Ohne laufende Warteschlange öffnet
 * auch der Abspielknopf die App (dort wählt man, was läuft).
 */
class AudioWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val s = AudioWidgetState.get(context)
        val cover = s.cover?.let { withContext(Dispatchers.IO) { App.get(context).audio.coverBlocking(it) } }
        val open = Intent(context, MainActivity::class.java).putExtra(MainActivity.EXTRA_TAB, "audio")
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        provideContent {
            GlanceTheme(colors = widgetColors(context)) {
                val height = LocalSize.current.height
                val wide = LocalSize.current.width >= 250.dp
                val art = (height - 16.dp).coerceIn(40.dp, 96.dp)
                Row(GlanceModifier.fillMaxSize().cornerRadius(20.dp).background(GlanceTheme.colors.widgetBackground).padding(8.dp),
                    verticalAlignment = Alignment.CenterVertically) {
                    Box(GlanceModifier.size(art).cornerRadius(12.dp).background(GlanceTheme.colors.secondaryContainer).clickable(actionStartActivity(open)),
                        contentAlignment = Alignment.Center) {
                        if (cover != null) Image(ImageProvider(cover), contentDescription = null, modifier = GlanceModifier.size(art))
                        else Image(ImageProvider(R.drawable.ic_audio_music_note), contentDescription = null, modifier = GlanceModifier.size(art / 2),
                            colorFilter = ColorFilter.tint(GlanceTheme.colors.onSecondaryContainer))
                    }
                    Spacer(GlanceModifier.width(10.dp))
                    Column(GlanceModifier.defaultWeight().fillMaxHeight().clickable(actionStartActivity(open)), verticalAlignment = Alignment.CenterVertically) {
                        Text(if (s.id != null) s.title else context.getString(R.string.audio), maxLines = 1,
                            style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 14.sp, fontWeight = FontWeight.Medium))
                        Text(if (s.id != null) s.artist else context.getString(R.string.widget_audio_idle), maxLines = 1,
                            style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp))
                    }
                    if (wide && s.id != null) Button(R.drawable.ic_audio_skip_previous, R.string.audio_prev, actionRunCallback<AudioPrevAction>())
                    Box(GlanceModifier.size(44.dp).cornerRadius(22.dp).background(GlanceTheme.colors.primary)
                        .clickable(if (s.id != null) actionRunCallback<AudioToggleAction>() else actionStartActivity(open)), contentAlignment = Alignment.Center) {
                        Image(ImageProvider(if (s.playing) R.drawable.ic_audio_pause else R.drawable.ic_audio_play),
                            contentDescription = context.getString(if (s.playing) R.string.audio_pause else R.string.audio_play),
                            modifier = GlanceModifier.size(26.dp), colorFilter = ColorFilter.tint(GlanceTheme.colors.onPrimary))
                    }
                    if (s.id != null && s.hasNext) Button(R.drawable.ic_audio_skip_next, R.string.audio_next, actionRunCallback<AudioNextAction>())
                }
            }
        }
    }

    @androidx.compose.runtime.Composable
    private fun Button(icon: Int, label: Int, action: androidx.glance.action.Action) {
        val ctx = androidx.glance.LocalContext.current
        Box(GlanceModifier.size(40.dp).cornerRadius(20.dp).clickable(action), contentAlignment = Alignment.Center) {
            Image(ImageProvider(icon), contentDescription = ctx.getString(label), modifier = GlanceModifier.size(26.dp),
                colorFilter = ColorFilter.tint(GlanceTheme.colors.onSurface))
        }
    }
}

class AudioWidgetReceiver : GlanceAppWidgetReceiver() { override val glanceAppWidget: GlanceAppWidget = AudioWidget() }

/** Befehl an den Wiedergabedienst über dieselbe Steuerung wie in der App; ohne Warteschlange (Dienst neu) zurück auf „nichts läuft“. */
private suspend fun command(context: Context, f: (Context) -> Unit) = withContext(Dispatchers.Main) {
    val app = context.applicationContext
    f(app)
    AudioPlayer.afterConnect(app) { count -> if (count == 0) AudioWidgetState.clear(app) }
}

class AudioToggleAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) = command(context) { AudioPlayer.toggle(it) }
}
class AudioNextAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) = command(context) { AudioPlayer.next(it) }
}
class AudioPrevAction : ActionCallback {
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) = command(context) { AudioPlayer.prev(it) }
}
