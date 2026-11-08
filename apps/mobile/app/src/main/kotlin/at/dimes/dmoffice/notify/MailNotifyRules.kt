package at.dimes.dmoffice.notify

import at.dimes.dmoffice.api.MailItem

/** Einstellung der Mail-Benachrichtigungen eines Kontos (nach Auswertung von Konto-Eigenheiten und Vorgabe). */
data class MailNotifyConfig(val mode: String, val preview: Boolean, val hideOnLock: Boolean)

/** Was ein Konto selbst eingestellt hat; null = gilt die Vorgabe fuer alle Konten. */
data class MailNotifyOverride(val mode: String? = null, val preview: Boolean? = null, val hideOnLock: Boolean? = null)

/** Reine Regeln der Mail-Benachrichtigungen je Konto - ohne Android, daher testbar. */
object MailNotifyRules {
    const val OFF = "off"; const val ALL = "all"; const val RELEVANT = "relevant"; const val VIP = "vip"
    val MODES = listOf(ALL, RELEVANT, VIP, OFF)

    fun resolve(own: MailNotifyOverride?, default: MailNotifyConfig) = MailNotifyConfig(
        mode = own?.mode?.takeIf { it in MODES } ?: default.mode,
        preview = own?.preview ?: default.preview,
        hideOnLock = own?.hideOnLock ?: default.hideOnLock,
    )

    /**
     * Ungelesene Mails mit hoeherer UID als beim letzten Mal, die nach [mode] gemeldet werden, aelteste zuerst;
     * bei mehr als [limit] nur die neuesten. "Relevant": Prio-Posteingang (focus) nicht ausdruecklich "Sonstige";
     * "VIP": Absender unter [vips] (Kleinschreibung).
     */
    fun fresh(items: List<MailItem>, lastUid: Long, mode: String, vips: Set<String>, limit: Int = 6,
              categories: List<at.dimes.dmoffice.api.MailCategory> = emptyList()): List<MailItem> {
        if (lastUid < 0 || mode == OFF) return emptyList()
        return items.asSequence()
            .filter { it.uid > lastUid && !it.seen && !silenced(categories, it.keywords) }
            .filter { when (mode) {
                RELEVANT -> it.focus != false
                VIP -> (it.from?.address?.lowercase() ?: "") in vips
                else -> true
            } }
            .sortedBy { it.uid }
            .toList().takeLast(limit)
    }

    /** Keine Benachrichtigung: Die Mail traegt Kategorien, und alle stehen auf "ohne Benachrichtigung" (wie auf dem Server). */
    fun silenced(categories: List<at.dimes.dmoffice.api.MailCategory>, keywords: List<String>): Boolean {
        val kw = keywords.map { it.lowercase() }.toSet()
        val mine = categories.filter { it.key.lowercase() in kw }
        return mine.isNotEmpty() && mine.all { it.quiet }
    }

    /** Kanal je Konto: Ton, Vibration und Wichtigkeit stellt Android dafuer einzeln ein. */
    fun channelId(account: String) = "mail.$account"
    fun groupId(account: String) = "mail.$account"

    /** Anzeigename des Kontos: Name, sonst Adresse, sonst Kennung. */
    fun label(name: String, email: String, id: String) = name.ifBlank { email.ifBlank { id } }
}
