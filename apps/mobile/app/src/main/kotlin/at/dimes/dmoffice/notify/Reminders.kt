package at.dimes.dmoffice.notify

import android.app.AlarmManager
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import at.dimes.dmoffice.App
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.Items
import at.dimes.dmoffice.data.KIND_TASK
import at.dimes.dmoffice.ui.Fmt
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import java.time.ZoneId

/**
 * Erinnerungen als lokale Benachrichtigungen. Nach jedem Sync wird der naechste
 * faellige Alarm neu geplant (ein Alarm zur Zeit, danach der uebernaechste).
 */
object Reminders {
    const val CHANNEL = "reminders"
    private const val REQ_NEXT = 1000
    private const val ACTION_FIRE = "at.dimes.dmoffice.REMINDER"
    private const val ACTION_SNOOZE = "at.dimes.dmoffice.SNOOZE"
    private const val ACTION_DONE = "at.dimes.dmoffice.DONE"

    fun ensureChannel(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java)
        val ch = NotificationChannel(CHANNEL, context.getString(R.string.channel_reminders), NotificationManager.IMPORTANCE_HIGH)
        ch.enableVibration(true)
        nm.createNotificationChannel(ch)
    }

    data class Due(val href: String, val at: Long, val title: String, val text: String, val task: Boolean)

    /** Alle anstehenden Erinnerungen der naechsten 14 Tage, zeitlich sortiert. */
    suspend fun upcoming(context: Context, from: Long): List<Due> = withContext(Dispatchers.IO) {
        val app = App.get(context)
        val zone = ZoneId.systemDefault()
        val horizon = from + 14L * 86400_000
        val out = mutableListOf<Due>()
        val items = app.db.items().withReminders(from - 86400_000)
        val cals = app.db.calendars().list().associate { it.href to it.color }
        for (it in items) {
            val before = (it.reminderSeconds ?: continue) * 1000
            if (it.kind == KIND_TASK) {
                val due = it.due ?: continue
                val at = due - before
                if (at >= from && at < horizon) out.add(Due(it.href, at, it.summary, context.getString(R.string.due_on, Fmt.dateTime(context, due, it.allDay)), true))
            } else {
                for (o in Items.expandEvents(listOf(it), cals, zone, from - 86400_000, horizon + before)) {
                    val at = o.start - before
                    if (at >= from && at < horizon) out.add(Due(it.href, at, it.summary, Fmt.timeRange(context, o.start, o.end, o.allDay), false))
                }
            }
        }
        out.sortedBy { it.at }
    }

    /** Naechsten Alarm setzen. Laeuft nach Sync, Aenderungen, Neustart und nach jedem Alarm. */
    suspend fun reschedule(context: Context) {
        val now = System.currentTimeMillis()
        val next = upcoming(context, now + 1000).firstOrNull()
        val am = context.getSystemService(AlarmManager::class.java)
        val pi = firePending(context)
        am.cancel(pi)
        if (next == null) return
        try {
            if (Build.VERSION.SDK_INT < 31 || am.canScheduleExactAlarms()) am.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, next.at, pi)
            else am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, next.at, pi)
        } catch (_: SecurityException) {
            am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, next.at, pi)
        }
    }

    private fun firePending(context: Context): PendingIntent =
        PendingIntent.getBroadcast(context, REQ_NEXT, Intent(context, ReminderReceiver::class.java).setAction(ACTION_FIRE),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)

    /** Alles anzeigen, was jetzt faellig ist (mit etwas Toleranz), dann den naechsten planen. */
    suspend fun fire(context: Context) {
        val now = System.currentTimeMillis()
        val due = upcoming(context, now - 10 * 60_000).filter { it.at <= now + 1000 }
        for (d in due) show(context, d)
        reschedule(context)
    }

    fun show(context: Context, d: Due, idOverride: Int? = null) {
        if (Build.VERSION.SDK_INT >= 33 &&
            context.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) return
        val id = idOverride ?: (d.href.hashCode() xor (d.at / 60000).toInt())
        val open = PendingIntent.getActivity(context, id, MainActivity.openIntent(context, d.href),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val b = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(App.get(context).prefs.settings.value.accent.toInt())
            .setContentTitle(d.title.ifBlank { context.getString(if (d.task) R.string.task else R.string.event) })
            .setContentText(d.text)
            .setSubText(if (d.task) context.getString(R.string.reminder_task_due) else null)
            .setContentIntent(open)
            .setAutoCancel(true)
            .setCategory(if (d.task) NotificationCompat.CATEGORY_REMINDER else NotificationCompat.CATEGORY_EVENT)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
        b.addAction(0, context.getString(R.string.snooze_10), snooze(context, d, id, 10))
        b.addAction(0, context.getString(R.string.snooze_60), snooze(context, d, id, 60))
        if (d.task) {
            val done = Intent(context, ReminderReceiver::class.java).setAction(ACTION_DONE).putExtra("href", d.href).putExtra("id", id)
            b.addAction(0, context.getString(R.string.done_check), PendingIntent.getBroadcast(context, id + 2, done, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
        }
        NotificationManagerCompat.from(context).notify(id, b.build())
    }

    private fun snooze(context: Context, d: Due, id: Int, minutes: Int): PendingIntent {
        val i = Intent(context, ReminderReceiver::class.java).setAction(ACTION_SNOOZE)
            .putExtra("href", d.href).putExtra("title", d.title).putExtra("text", d.text).putExtra("task", d.task).putExtra("minutes", minutes).putExtra("id", id)
        return PendingIntent.getBroadcast(context, id * 31 + minutes, i, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    }

    fun handle(context: Context, intent: Intent) {
        when (intent.action) {
            ACTION_FIRE -> runBlocking { fire(context) }
            ACTION_SNOOZE -> {
                val id = intent.getIntExtra("id", 0)
                NotificationManagerCompat.from(context).cancel(id)
                val d = Due(intent.getStringExtra("href") ?: return, 0, intent.getStringExtra("title") ?: "", intent.getStringExtra("text") ?: "", intent.getBooleanExtra("task", false))
                val minutes = intent.getIntExtra("minutes", 10)
                val at = System.currentTimeMillis() + minutes * 60_000L
                val i = Intent(context, ReminderReceiver::class.java).setAction(ACTION_SNOOZED)
                    .putExtra("href", d.href).putExtra("title", d.title).putExtra("text", d.text).putExtra("task", d.task).putExtra("id", id)
                val pi = PendingIntent.getBroadcast(context, id, i, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
                val am = context.getSystemService(AlarmManager::class.java)
                try { am.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, pi) } catch (_: SecurityException) { am.set(AlarmManager.RTC_WAKEUP, at, pi) }
            }
            ACTION_SNOOZED -> {
                val d = Due(intent.getStringExtra("href") ?: return, 0, intent.getStringExtra("title") ?: "", intent.getStringExtra("text") ?: "", intent.getBooleanExtra("task", false))
                show(context, d, intent.getIntExtra("id", 0))
            }
            ACTION_DONE -> {
                val href = intent.getStringExtra("href") ?: return
                NotificationManagerCompat.from(context).cancel(intent.getIntExtra("id", 0))
                runBlocking { App.get(context).repo.setCompleted(href, true) }
            }
        }
    }

    private const val ACTION_SNOOZED = "at.dimes.dmoffice.SNOOZED"
}

class ReminderReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val pending = goAsync()
        Thread {
            try { Reminders.handle(context, intent) } finally { pending.finish() }
        }.start()
    }
}

class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val pending = goAsync()
        Thread {
            try { runBlocking { Reminders.reschedule(context) } } finally { pending.finish() }
        }.start()
    }
}
