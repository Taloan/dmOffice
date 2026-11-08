package at.dimes.dmoffice.coach

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.util.Log
import at.dimes.dmoffice.App
import at.dimes.dmoffice.ui.currentLocation
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Ort fürs Wetter bei Pukkis Bewegungs-Erinnerung (Coach, nur wenn eingeschaltet): beim Öffnen der App einmal den ungefähren
 * Ort an den Server schicken - höchstens alle zwei Stunden, kein Verfolgen im Hintergrund. Der Server rundet auf etwa einen
 * Kilometer und nimmt ihn sechs Stunden lang, sonst den Wohnort.
 */
object CoachLocation {
    private const val PREFS = "coach_location"

    fun enabled(ctx: Context) = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean("enabled", false)
    fun setEnabled(ctx: Context, on: Boolean) = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean("enabled", on).apply()

    fun permitted(ctx: Context) = ctx.checkSelfPermission(Manifest.permission.ACCESS_COARSE_LOCATION) == PackageManager.PERMISSION_GRANTED

    /** Beim Öffnen der App: schicken, wenn eingeschaltet, erlaubt und der letzte Ort älter als zwei Stunden ist. */
    suspend fun sendIfWanted(ctx: Context, force: Boolean = false) {
        if (!enabled(ctx) || !permitted(ctx)) return
        val prefs = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        if (!force && System.currentTimeMillis() - prefs.getLong("sent", 0L) < 2 * 3600_000L) return
        val repo = App.get(ctx).coach
        try {
            val loc = currentLocation(ctx) ?: return
            withContext(Dispatchers.IO) { repo.sendLocation(loc.latitude, loc.longitude) }
            prefs.edit().putLong("sent", System.currentTimeMillis()).apply()
        } catch (e: Exception) { Log.w("CoachLocation", "Ort senden", e) }
    }
}
