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
import at.dimes.dmoffice.api.MailItem
import kotlinx.coroutines.flow.first

/**
 * Benachrichtigungen fuer neue Mails im Posteingang: nach jedem Abgleich (alle 15 Minuten, bei Push sofort)
 * die ungelesenen Mails mit hoeherer UID als beim letzten Mal. Einstellbar: aus, alle, nur von VIP-Kontakten;
 * mit oder ohne Textauszug; auf dem Sperrbildschirm mit Inhalt oder nur "Neue E-Mail".
 */
object MailNotify {
    const val CHANNEL = "mail"
    const val OFF = "off"; const val ALL = "all"; const val VIP = "vip"
    const val LOCK_SHOW = "show"; const val LOCK_HIDE = "hide"

    fun ensureChannel(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, context.getString(R.string.channel_mail), NotificationManager.IMPORTANCE_HIGH))
    }

    private fun sp(context: Context) = context.getSharedPreferences("mailnotify", Context.MODE_PRIVATE)

    /** Nach dem Abgleich des Posteingangs von [account]. Beim ersten Mal nur den Stand merken, nicht melden. */
    suspend fun check(context: Context, account: String) {
        val app = App.get(context)
        val s = app.prefs.settings.value
        val items = app.mail.items(account, "INBOX").first()
        val maxUid = items.maxOfOrNull { it.uid } ?: return
        val key = "last.$account"
        val last = sp(context).getLong(key, -1)
        sp(context).edit().putLong(key, maxOf(last, maxUid)).apply()
        if (last < 0 || s.mailNotify == OFF) return
        val fresh = items.filter { it.uid > last && !it.seen }.sortedBy { it.uid }
        if (fresh.isEmpty()) return
        val vips = if (s.mailNotify == VIP) vipAddresses(context) else null
        for (m in fresh.takeLast(6)) {
            if (vips != null && (m.from?.address?.lowercase() ?: "") !in vips) continue
            show(context, account, m, s.mailPreview, s.mailLockscreen == LOCK_HIDE)
        }
    }

    /** Adressen der VIP-Kontakte - aus dem lokalen Stand der Kontakte (wie im Web markiert). */
    suspend fun vipAddresses(context: Context): Set<String> =
        App.get(context).contacts.allNow().filter { it.vip }.flatMap { c -> c.emails.map { it.value.trim().lowercase() } }.toSet()

    private fun show(context: Context, account: String, m: MailItem, preview: Boolean, hideOnLock: Boolean) {
        if (Build.VERSION.SDK_INT >= 33 &&
            context.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) return
        val id = ("mail/$account/${m.uid}").hashCode()
        val open = PendingIntent.getActivity(context, id, MainActivity.mailIntent(context, account, "INBOX", m.uid),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val color = App.get(context).prefs.settings.value.accent.toInt()
        val who = m.from?.text?.ifBlank { null } ?: context.getString(R.string.new_mail)
        val subject = m.subject.ifBlank { "(…)" }
        val b = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(color)
            .setContentTitle(who)
            .setContentText(subject)
            .setWhen(System.currentTimeMillis())
            .setContentIntent(open)
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_EMAIL)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setGroup("mail")
        // Textauszug: Betreff fett, darunter die Vorschau der Mail
        if (preview && m.preview.isNotBlank()) b.setStyle(NotificationCompat.BigTextStyle().bigText(subject + "\n" + m.preview))
        // Sperrbildschirm: entweder alles zeigen oder nur "Neue E-Mail"
        if (hideOnLock) {
            b.setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            b.setPublicVersion(NotificationCompat.Builder(context, CHANNEL).setSmallIcon(R.drawable.ic_notification).setColor(color)
                .setContentTitle(context.getString(R.string.app_name)).setContentText(context.getString(R.string.new_mail)).build())
        } else b.setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
        NotificationManagerCompat.from(context).notify(id, b.build())
    }
}
