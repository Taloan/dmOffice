package at.dimes.dmoffice.notify

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import at.dimes.dmoffice.App
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.R
import at.dimes.dmoffice.chat.ChatMessage
import at.dimes.dmoffice.chat.ChatRoom

/**
 * Benachrichtigungen fuer neue Chat-Nachrichten (Version 2): nach dem Signal t=chat vom Server bzw. beim Abgleich
 * je Unterhaltung die neueste ungelesene Nachricht, die noch nicht gemeldet wurde. Eine Benachrichtigung je
 * Unterhaltung (die naechste ersetzt sie); ist die Unterhaltung gerade offen, keine. Stumme Unterhaltungen nie.
 */
object ChatNotify {
    const val CHANNEL = "chat"

    /** Unterhaltung, die gerade auf dem Bildschirm ist - dafuer keine Benachrichtigung. */
    @Volatile var openRoom: Long? = null

    fun ensureChannel(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, context.getString(R.string.channel_chat), NotificationManager.IMPORTANCE_HIGH))
    }

    private fun sp(context: Context) = context.getSharedPreferences("chatnotify", Context.MODE_PRIVATE)

    /** Schalter in den Chat-Einstellungen (Vorgabe an). */
    fun enabled(context: Context) = sp(context).getBoolean("enabled", true)
    fun setEnabled(context: Context, on: Boolean) = sp(context).edit().putBoolean("enabled", on).apply()

    /** Darf die App überhaupt Benachrichtigungen zeigen (Erlaubnis ab Android 13, Kanal „Chat“ nicht abgeschaltet)? */
    fun allowedBySystem(context: Context): Boolean {
        if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) return false
        val ch = context.getSystemService(NotificationManager::class.java).getNotificationChannel(CHANNEL)
        return ch == null || ch.importance != NotificationManager.IMPORTANCE_NONE
    }

    private fun notificationId(room: Long) = ("chat/$room").hashCode()

    /** Gelesen (auch an einem anderen Geraet): Benachrichtigung der Unterhaltung weg. */
    fun cancel(context: Context, room: Long) = NotificationManagerCompat.from(context).cancel(notificationId(room))

    suspend fun check(context: Context) {
        if (!enabled(context)) return
        val app = App.get(context)
        val me = app.chat.whoAmI()
        // Ruhezeit: nichts melden (und nichts als gemeldet merken - danach kommt es)
        val quiet = try { app.chat.status().quiet } catch (_: Exception) { null }
        if (quiet == "all") return
        val rooms = app.chat.rooms()
        val prefs = sp(context)
        val edit = prefs.edit()
        val now = System.currentTimeMillis()
        for (r in rooms) {
            val last = r.last ?: continue
            val key = "last.${r.id}"
            val notified = prefs.getLong(key, -1L)
            if (r.unread == 0) { cancel(context, r.id); edit.putLong(key, maxOf(notified, last.id)); continue }
            // Stumm: nur, wenn ich erwaehnt bin
            if (((r.muted || quiet == "mentions") && r.mentioned == 0) || last.isSystem || last.sender == me || last.id <= notified || r.id == openRoom) continue
            // Beim ersten Mal nichts Altes melden - nur, was gerade erst kam
            if (notified < 0 && now - last.createdMs > 10 * 60_000) { edit.putLong(key, last.id); continue }
            show(context, r, last)
            edit.putLong(key, last.id)
        }
        edit.apply()
    }

    private fun show(context: Context, r: ChatRoom, m: ChatMessage) {
        if (Build.VERSION.SDK_INT >= 33 &&
            context.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) return
        ensureChannel(context)
        val id = notificationId(r.id)
        val open = PendingIntent.getActivity(context, id, MainActivity.routeIntent(context, "chat/${r.id}"),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val title = if (r.isGroup) "${m.senderName} · ${r.title}" else r.title
        val body = m.text ?: m.refs.joinToString(", ") { it.preview }.ifBlank { context.getString(R.string.chat_new_message) }
        val more = if (r.unread > 1) context.getString(R.string.chat_unread_count, r.unread) else null
        val b = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(App.get(context).prefs.settings.value.accent.toInt())
            .setContentTitle(title)
            .setContentText(body)
            .setSubText(more)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setWhen(m.createdMs.takeIf { it > 0 } ?: System.currentTimeMillis())
            .setContentIntent(open)
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setPublicVersion(NotificationCompat.Builder(context, CHANNEL).setSmallIcon(R.drawable.ic_notification)
                .setContentTitle(context.getString(R.string.chat_new_message)).build())
        NotificationManagerCompat.from(context).notify(id, b.build())
    }
}
