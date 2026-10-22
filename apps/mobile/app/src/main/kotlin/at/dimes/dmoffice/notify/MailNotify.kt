package at.dimes.dmoffice.notify

import android.app.NotificationChannel
import android.app.NotificationChannelGroup
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import at.dimes.dmoffice.App
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.MailAccount
import at.dimes.dmoffice.api.MailItem
import kotlinx.coroutines.flow.first

/**
 * Benachrichtigungen fuer neue Mails im Posteingang, je Mailkonto getrennt: nach jedem Abgleich (alle 15 Minuten,
 * bei Push sofort) die ungelesenen Mails mit hoeherer UID als beim letzten Mal. Je Konto einstellbar (Vorgabe aus
 * den allgemeinen Mail-Einstellungen): aus, alle, nur Relevant (Prio-Posteingang), nur von VIP-Kontakten; mit oder
 * ohne Textauszug; auf dem Sperrbildschirm mit Inhalt oder nur "Neue E-Mail". Jedes Konto hat einen eigenen
 * Android-Kanal (Ton, Vibration, Wichtigkeit in den Systemeinstellungen) und eine eigene Gruppe: mehrere Mails
 * eines Kontos werden gebuendelt, der Kontoname steht als Untertitel.
 */
object MailNotify {
    /** Kanal aus der Zeit vor den Konto-Kanaelen; wird entfernt, sobald Konto-Kanaele da sind. */
    private const val LEGACY_CHANNEL = "mail"
    const val OFF = MailNotifyRules.OFF; const val ALL = MailNotifyRules.ALL; const val VIP = MailNotifyRules.VIP
    const val LOCK_SHOW = "show"; const val LOCK_HIDE = "hide"

    /** Kanaele und Gruppen fuer die zuletzt bekannten Konten anlegen bzw. umbenennen. */
    fun ensureChannels(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java)
        val known = MailNotifyPrefs.get(context).known()
        if (known.isEmpty()) {
            // Noch keine Konten bekannt (erster Start): Sammelkanal wie bisher
            nm.createNotificationChannel(NotificationChannel(LEGACY_CHANNEL, context.getString(R.string.channel_mail), NotificationManager.IMPORTANCE_HIGH))
            return
        }
        for (a in known) {
            nm.createNotificationChannelGroup(NotificationChannelGroup(MailNotifyRules.groupId(a.id), a.label))
            val ch = NotificationChannel(MailNotifyRules.channelId(a.id), a.label, NotificationManager.IMPORTANCE_HIGH)
            ch.group = MailNotifyRules.groupId(a.id)
            ch.description = context.getString(R.string.channel_mail_account, a.label)
            nm.createNotificationChannel(ch)
        }
        nm.deleteNotificationChannel(LEGACY_CHANNEL)
    }

    /** Konten vom Server uebernehmen: merken, Kanaele anlegen und nicht mehr vorhandene Konten entfernen. */
    fun syncAccounts(context: Context, accounts: List<MailAccount>) {
        val prefs = MailNotifyPrefs.get(context)
        val old = prefs.known()
        prefs.remember(accounts.map { MailNotifyPrefs.Known(it.id, MailNotifyRules.label(it.name, it.email, it.id), it.email) })
        val nm = context.getSystemService(NotificationManager::class.java)
        for (gone in old.filter { o -> accounts.none { it.id == o.id } }) nm.deleteNotificationChannelGroup(MailNotifyRules.groupId(gone.id))
        ensureChannels(context)
    }

    /** Vorgabe fuer alle Konten (Einstellungen > E-Mail). */
    fun defaults(context: Context): MailNotifyConfig {
        val s = App.get(context).prefs.settings.value
        return MailNotifyConfig(s.mailNotify, s.mailPreview, s.mailLockscreen == LOCK_HIDE)
    }

    /** Einstellungen des Kontos: seine eigenen, sonst die Vorgabe. */
    fun config(context: Context, account: String): MailNotifyConfig =
        MailNotifyRules.resolve(MailNotifyPrefs.get(context).override(account), defaults(context))

    private fun sp(context: Context) = context.getSharedPreferences("mailnotify", Context.MODE_PRIVATE)

    /** Nach dem Abgleich des Posteingangs von [account]. Beim ersten Mal nur den Stand merken, nicht melden. */
    suspend fun check(context: Context, account: String) {
        val app = App.get(context)
        val items = app.mail.items(account, "INBOX").first()
        val maxUid = items.maxOfOrNull { it.uid } ?: return
        val key = "last.$account"
        val last = sp(context).getLong(key, -1)
        sp(context).edit().putLong(key, maxOf(last, maxUid)).apply()
        val cfg = config(context, account)
        if (last < 0 || cfg.mode == OFF) return
        val vips = if (cfg.mode == VIP) vipAddresses(context) else emptySet()
        val fresh = MailNotifyRules.fresh(items, last, cfg.mode, vips)
        if (fresh.isEmpty()) return
        val label = MailNotifyPrefs.get(context).known().firstOrNull { it.id == account }?.label ?: account
        showAll(context, account, label, fresh, cfg)
    }

    /** Adressen der VIP-Kontakte - aus dem lokalen Stand der Kontakte (wie im Web markiert). */
    suspend fun vipAddresses(context: Context): Set<String> =
        App.get(context).contacts.allNow().filter { it.vip }.flatMap { c -> c.emails.map { it.value.trim().lowercase() } }.toSet()

    /** Oeffnet die Android-Einstellungen des Kanals dieses Kontos (Ton, Vibration, Wichtigkeit). */
    fun channelSettings(context: Context, account: String): android.content.Intent {
        ensureChannels(context)
        return android.content.Intent(android.provider.Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS)
            .putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, context.packageName)
            .putExtra(android.provider.Settings.EXTRA_CHANNEL_ID, MailNotifyRules.channelId(account))
    }

    private fun showAll(context: Context, account: String, label: String, mails: List<MailItem>, cfg: MailNotifyConfig) {
        if (Build.VERSION.SDK_INT >= 33 &&
            context.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) return
        ensureChannels(context)
        val channel = if (MailNotifyPrefs.get(context).known().isEmpty()) LEGACY_CHANNEL else MailNotifyRules.channelId(account)
        val group = MailNotifyRules.groupId(account)
        val color = App.get(context).prefs.settings.value.accent.toInt()
        val nm = NotificationManagerCompat.from(context)
        val ids = mutableSetOf<Int>()
        for (m in mails) {
            val id = ("mail/$account/${m.uid}").hashCode()
            ids += id
            val open = PendingIntent.getActivity(context, id, MainActivity.mailIntent(context, account, "INBOX", m.uid),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
            val who = m.from?.text?.ifBlank { null } ?: context.getString(R.string.new_mail)
            val subject = m.subject.ifBlank { "(…)" }
            val b = NotificationCompat.Builder(context, channel)
                .setSmallIcon(R.drawable.ic_notification)
                .setColor(color)
                .setContentTitle(who)
                .setContentText(subject)
                .setSubText(label)
                .setWhen(System.currentTimeMillis())
                .setContentIntent(open)
                .setAutoCancel(true)
                .setCategory(NotificationCompat.CATEGORY_EMAIL)
                .setPriority(NotificationCompat.PRIORITY_HIGH)
                .setGroup(group)
                .setGroupAlertBehavior(NotificationCompat.GROUP_ALERT_CHILDREN)
            // Textauszug: Betreff fett, darunter die Vorschau der Mail
            if (cfg.preview && m.preview.isNotBlank()) b.setStyle(NotificationCompat.BigTextStyle().bigText(subject + "\n" + m.preview))
            lockscreen(context, b, channel, color, cfg.hideOnLock, label)
            nm.notify(id, b.build())
        }
        // Sammelmeldung des Kontos: Anzahl und die neuesten Absender; Tippen oeffnet die neueste Mail
        val summaryId = ("mailsum/$account").hashCode()
        val active = context.getSystemService(NotificationManager::class.java).activeNotifications
            .filter { it.notification.group == group && it.id != summaryId }.map { it.id }.toSet()
        val count = (active + ids).size
        val open = PendingIntent.getActivity(context, summaryId, MainActivity.mailIntent(context, account, "INBOX", mails.last().uid),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val countText = context.resources.getQuantityString(R.plurals.n_new_mails, count, count)
        val inbox = NotificationCompat.InboxStyle().setBigContentTitle(label).setSummaryText(countText)
        if (!cfg.hideOnLock) for (m in mails.takeLast(5).reversed()) inbox.addLine((m.from?.text?.ifBlank { null } ?: "?") + "  " + m.subject)
        val sum = NotificationCompat.Builder(context, channel)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(color)
            .setContentTitle(label)
            .setContentText(countText)
            .setSubText(label)
            .setStyle(inbox)
            .setContentIntent(open)
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_EMAIL)
            .setGroup(group).setGroupSummary(true)
            .setGroupAlertBehavior(NotificationCompat.GROUP_ALERT_CHILDREN)
        lockscreen(context, sum, channel, color, cfg.hideOnLock, label)
        nm.notify(summaryId, sum.build())
    }

    /** Sperrbildschirm: entweder alles zeigen oder nur "Neue E-Mail" (mit dem Kontonamen). */
    private fun lockscreen(context: Context, b: NotificationCompat.Builder, channel: String, color: Int, hide: Boolean, label: String) {
        if (hide) {
            b.setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            b.setPublicVersion(NotificationCompat.Builder(context, channel).setSmallIcon(R.drawable.ic_notification).setColor(color)
                .setContentTitle(context.getString(R.string.new_mail)).setContentText(label).build())
        } else b.setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
    }
}
