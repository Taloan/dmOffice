package at.dimes.dmoffice.update

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import at.dimes.dmoffice.App
import at.dimes.dmoffice.BuildConfig
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.R
import java.util.concurrent.TimeUnit

/** Einmal am Tag im Hintergrund nach einer neuen Version fragen und sie per Benachrichtigung melden. */
class UpdateWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        if (!Updater.autoEnabled(applicationContext)) return Result.success()
        val r = try { Updater.check(applicationContext) } catch (_: Exception) { return Result.success() }
        if (UpdateLogic.isNewer(r, BuildConfig.VERSION_CODE) && Updater.shouldNotify(applicationContext, r)) notify(applicationContext, r)
        return Result.success()
    }

    companion object {
        private const val PERIODIC = "update-check"
        const val CHANNEL = "update"
        private const val NOTIFICATION_ID = 0x0d0ffee

        fun ensureChannel(context: Context) {
            context.getSystemService(NotificationManager::class.java)
                .createNotificationChannel(NotificationChannel(CHANNEL, context.getString(R.string.channel_update), NotificationManager.IMPORTANCE_DEFAULT))
        }

        /** Taeglich pruefen, wenn eingeschaltet; die Debug-Variante nie. */
        fun schedule(context: Context) {
            val wm = WorkManager.getInstance(context)
            if (!Updater.autoEnabled(context)) { wm.cancelUniqueWork(PERIODIC); return }
            val constraints = Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()
            val req = PeriodicWorkRequestBuilder<UpdateWorker>(1, TimeUnit.DAYS).setConstraints(constraints).build()
            wm.enqueueUniquePeriodicWork(PERIODIC, ExistingPeriodicWorkPolicy.KEEP, req)
        }

        private fun notify(context: Context, r: Release) {
            if (Build.VERSION.SDK_INT >= 33 &&
                context.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) return
            val open = PendingIntent.getActivity(context, 1, MainActivity.updateIntent(context, install = false),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            val install = PendingIntent.getActivity(context, 2, MainActivity.updateIntent(context, install = true),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            val title = context.getString(R.string.update_available_title)
            val text = context.getString(R.string.update_notif_text, r.versionName)
            val b = NotificationCompat.Builder(context, CHANNEL)
                .setSmallIcon(R.drawable.ic_notification)
                .setColor(App.get(context).prefs.settings.value.accent.toInt())
                .setContentTitle(title)
                .setContentText(text)
                .setContentIntent(open)
                .setAutoCancel(true)
                .addAction(0, context.getString(R.string.update_now), install)
            if (r.notes.isNotBlank()) b.setStyle(NotificationCompat.BigTextStyle().bigText(text + "\n\n" + r.notes))
            NotificationManagerCompat.from(context).notify(NOTIFICATION_ID, b.build())
        }

        fun cancelNotification(context: Context) = NotificationManagerCompat.from(context).cancel(NOTIFICATION_ID)
    }
}
