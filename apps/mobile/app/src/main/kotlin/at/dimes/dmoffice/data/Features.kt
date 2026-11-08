package at.dimes.dmoffice.data

import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import androidx.compose.runtime.mutableStateOf
import at.dimes.dmoffice.widget.AudioWidgetReceiver
import at.dimes.dmoffice.widget.CoachWidgetLargeReceiver
import at.dimes.dmoffice.widget.CoachWidgetReceiver
import at.dimes.dmoffice.widget.JournalNewWidgetReceiver
import at.dimes.dmoffice.widget.JournalPhotoWidgetReceiver
import at.dimes.dmoffice.widget.TasksWidgetReceiver

/**
 * Release-Liste (Server: Core/Features.cs): was der Server in seiner Version freigeschaltet hat. Kommt mit /api/auth/state
 * („featuresOff“, dazu „files“ = Dateiablage für dieses Konto) und wird gemerkt, damit Reiter und Widgets auch ohne
 * Verbindung stimmen. Nicht Freigeschaltetes blendet die App aus: Reiter, Einstellungen, Widgets der Übersicht und die
 * Widgets des Startbildschirms (aus der Auswahl genommen). Vom Admin ausgeschaltete Module (Hauptschalter) kommen genauso
 * in „featuresOff“.
 */
object Features {
    const val JOURNAL = "journal"
    const val COACH = "coach"
    const val TASKS = "tasks"
    const val NOTES = "notes"
    const val GALLERY = "gallery"
    const val AUDIO = "audio"
    const val DMS = "dms"

    val off = mutableStateOf<Set<String>>(emptySet())
    /** Dateiablage für dieses Konto abgeschaltet (Verwaltung) - dann auch keine Galerie. */
    val filesOff = mutableStateOf(false)
    private var loaded = false

    fun on(id: String) = id !in off.value

    private fun sp(ctx: Context) = ctx.getSharedPreferences("features", Context.MODE_PRIVATE)

    fun load(ctx: Context) {
        if (loaded) return
        loaded = true
        val p = sp(ctx)
        off.value = p.getStringSet("off", emptySet())!!.toSet()
        filesOff.value = p.getBoolean("filesOff", false)
    }

    /** Neuer Stand vom Server; true, wenn sich etwas geändert hat (dann Reiter neu ordnen). */
    fun update(ctx: Context, newOff: Set<String>, files: Boolean): Boolean {
        load(ctx)
        if (newOff == off.value && !files == filesOff.value) return false
        off.value = newOff
        filesOff.value = !files
        sp(ctx).edit().putStringSet("off", newOff).putBoolean("filesOff", !files).apply()
        widgets(ctx)
        return true
    }

    /** Widgets des Startbildschirms nicht freigeschalteter Bereiche aus der Auswahl nehmen (bzw. wieder anbieten). */
    private fun widgets(ctx: Context) {
        val pm = ctx.packageManager
        fun set(cls: Class<*>, enabled: Boolean) = try {
            pm.setComponentEnabledSetting(ComponentName(ctx, cls),
                if (enabled) PackageManager.COMPONENT_ENABLED_STATE_DEFAULT else PackageManager.COMPONENT_ENABLED_STATE_DISABLED,
                PackageManager.DONT_KILL_APP)
        } catch (_: Exception) { }
        val journal = on(JOURNAL)
        set(JournalPhotoWidgetReceiver::class.java, journal)
        set(JournalNewWidgetReceiver::class.java, journal)
        val coach = on(COACH)
        set(CoachWidgetReceiver::class.java, coach)
        set(CoachWidgetLargeReceiver::class.java, coach)
        set(TasksWidgetReceiver::class.java, on(TASKS))
        set(AudioWidgetReceiver::class.java, on(AUDIO))
    }
}
