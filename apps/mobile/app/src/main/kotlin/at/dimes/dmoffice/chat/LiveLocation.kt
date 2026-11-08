package at.dimes.dmoffice.chat

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.os.Build
import android.os.IBinder
import android.os.Looper
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import at.dimes.dmoffice.App
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.ApiException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import java.text.DateFormat
import java.util.Date

/**
 * Standort live teilen (1-30 Minuten): ein Vordergrunddienst mit Benachrichtigung "Standort wird geteilt - Beenden"
 * schickt neue Positionen in dieselbe Nachricht (PUT /api/chat/rooms/{room}/location/{msg}), hoechstens alle 15 Sekunden.
 * Endet nach Ablauf, beim Beenden (dann auch auf dem Server) oder wenn der Server nichts mehr annimmt (zurueckgezogen,
 * geloescht, abgelaufen). Immer nur ein Teilen zugleich - ein neues beendet das alte.
 */
object LiveLocation {
    data class Share(val room: Long, val message: Long, val until: Long, val title: String)

    internal val _active = MutableStateFlow<Share?>(null)
    /** Was gerade geteilt wird (null: nichts) - fuer "Beenden" an der Nachricht. */
    val active: StateFlow<Share?> = _active

    internal const val CHANNEL = "location"
    internal const val NOTIFICATION = 4711
    internal const val START = "at.dimes.dmoffice.chat.LIVE_START"
    internal const val STOP = "at.dimes.dmoffice.chat.LIVE_STOP"

    fun start(ctx: Context, share: Share) {
        val i = Intent(ctx, LiveLocationService::class.java).setAction(START)
            .putExtra("room", share.room).putExtra("message", share.message).putExtra("until", share.until).putExtra("title", share.title)
        ctx.startForegroundService(i)
    }

    /** Vorzeitig beenden (auch auf dem Server). */
    fun stop(ctx: Context) {
        if (_active.value == null) return
        ctx.startService(Intent(ctx, LiveLocationService::class.java).setAction(STOP))
    }
}

class LiveLocationService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var share: LiveLocation.Share? = null
    private var listener: LocationListener? = null
    private var latest: Location? = null
    private var sent: Location? = null
    private var job: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            LiveLocation.START -> {
                val next = LiveLocation.Share(intent.getLongExtra("room", 0), intent.getLongExtra("message", 0),
                    intent.getLongExtra("until", 0), intent.getStringExtra("title") ?: "")
                // Ein anderes Teilen lief noch: dort beenden
                share?.let { old -> if (old.message != next.message) stopRemote(old) }
                begin(next)
            }
            LiveLocation.STOP -> { share?.let { stopRemote(it) }; end() }
            else -> end()
        }
        return START_NOT_STICKY
    }

    private fun begin(s: LiveLocation.Share) {
        share = s
        LiveLocation._active.value = s
        ensureChannel(this)
        try {
            ServiceCompat.startForeground(this, LiveLocation.NOTIFICATION, notification(s),
                if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_LOCATION else 0)
        } catch (e: Exception) {
            // Android 14+: ohne Standort-Berechtigung oder aus dem Hintergrund kein Dienst dieser Art
            Log.w(TAG, "startForeground", e); end(); return
        }
        listen()
        job?.cancel()
        job = scope.launch {
            while (System.currentTimeMillis() < s.until) {
                val loc = latest
                if (loc != null && loc !== sent && !send(s, loc)) return@launch
                delay(15_000L.coerceAtMost(maxOf(1_000L, s.until - System.currentTimeMillis())))
            }
            end()
        }
    }

    /** false: der Server nimmt nichts mehr an - Teilen ist dort zu Ende. */
    private suspend fun send(s: LiveLocation.Share, loc: Location): Boolean {
        try {
            App.get(this).chat.updateLocation(s.room, s.message, loc.latitude, loc.longitude, if (loc.hasAccuracy()) loc.accuracy.toDouble() else null)
            sent = loc
        } catch (e: ApiException) {
            if (e.code in 400..499) { end(); return false }
        } catch (e: Exception) { Log.w(TAG, "update", e) } // kein Netz: beim naechsten Mal
        return true
    }

    @Suppress("MissingPermission")
    private fun listen() {
        if (listener != null) return
        val lm = getSystemService(LocationManager::class.java) ?: return
        val l = LocationListener { loc -> val old = latest; if (old == null || loc.time >= old.time || loc.accuracy < old.accuracy) latest = loc }
        val providers = buildList {
            if (Build.VERSION.SDK_INT >= 31 && enabled(lm, LocationManager.FUSED_PROVIDER)) add(LocationManager.FUSED_PROVIDER)
            else { if (enabled(lm, LocationManager.GPS_PROVIDER)) add(LocationManager.GPS_PROVIDER); if (enabled(lm, LocationManager.NETWORK_PROVIDER)) add(LocationManager.NETWORK_PROVIDER) }
        }
        try {
            for (p in providers) lm.requestLocationUpdates(p, 10_000L, 5f, l, Looper.getMainLooper())
            listener = l
        } catch (e: SecurityException) { Log.w(TAG, "listen", e); end() }
    }

    private fun enabled(lm: LocationManager, p: String) = try { lm.isProviderEnabled(p) } catch (_: Exception) { false }

    private fun stopRemote(s: LiveLocation.Share) {
        val app = App.get(this)
        // Ueberlebt das Ende des Dienstes
        CoroutineScope(Dispatchers.IO).launch { try { app.chat.stopLocation(s.room, s.message) } catch (e: Exception) { Log.w(TAG, "stop", e) } }
    }

    private fun end() {
        job?.cancel(); job = null
        listener?.let { getSystemService(LocationManager::class.java)?.removeUpdates(it) }
        listener = null
        share = null; latest = null; sent = null
        LiveLocation._active.value = null
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    override fun onDestroy() {
        listener?.let { getSystemService(LocationManager::class.java)?.removeUpdates(it) }
        listener = null
        LiveLocation._active.value = null
        scope.cancel()
        super.onDestroy()
    }

    private fun notification(s: LiveLocation.Share) = NotificationCompat.Builder(this, LiveLocation.CHANNEL)
        .setSmallIcon(R.drawable.ic_notification)
        .setColor(App.get(this).prefs.settings.value.accent.toInt())
        .setContentTitle(getString(R.string.chat_location_sharing))
        .setContentText(getString(R.string.chat_location_sharing_text, s.title, DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(s.until))))
        // Restzeit als rueckwaerts laufende Uhr
        .setWhen(s.until).setShowWhen(true).setUsesChronometer(true).setChronometerCountDown(true)
        .setOngoing(true)
        .setOnlyAlertOnce(true)
        .setCategory(NotificationCompat.CATEGORY_SERVICE)
        .setContentIntent(PendingIntent.getActivity(this, LiveLocation.NOTIFICATION, MainActivity.routeIntent(this, "chat/${s.room}"),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
        .addAction(0, getString(R.string.chat_location_stop), PendingIntent.getService(this, LiveLocation.NOTIFICATION,
            Intent(this, LiveLocationService::class.java).setAction(LiveLocation.STOP), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
        .build()

    companion object {
        private const val TAG = "LiveLocation"
        fun ensureChannel(ctx: Context) {
            ctx.getSystemService(NotificationManager::class.java)
                .createNotificationChannel(NotificationChannel(LiveLocation.CHANNEL, ctx.getString(R.string.channel_location), NotificationManager.IMPORTANCE_LOW))
        }
    }
}
